using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Episodic;
using PetBox.Sessions.Search;
using PetBox.Sessions.Services;
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Tests.Sessions;

// Regression coverage for card memory-telemetry-blind-paths #2 (spec memory-usage-observability):
// session_search's digest-discovery leg reads the `session-digests` memory store as one of three
// RRF legs but never told entry_usage a digest was reached — the superseded card
// usage-counters-blind-to-session-search measured 289/352 digests reading as a dead tail purely
// from missing telemetry, not disuse. GC only ever touches the `autocaptured` store (that
// invariant lives in MemoryQuarantineGcJobTests and is untouched here), so nothing here risked
// pruning session-digests — but the store's own usage aggregate lied about it being dead.
//
// Fixture/class split mirrors SessionSearchCursorTests (share-fixtures-across-per-test-classes):
// the fixture owns the expensive migrated DB files, the test class rebuilds the thin service
// wrappers (including the usage recorder, since it is stateful per test — a leftover enqueued
// event from a previous test must not bleed into this one's assertions).
public sealed class SessionDigestUsageFixture : IDisposable
{
	public const string Proj = "proj";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<SessionsDb> SessionsFactory { get; }
	public ScopedDbFactory<MemoryDb> MemoryFactory { get; }

	public SessionDigestUsageFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessdigusage-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		Db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		Db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		SessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), TestSchema.Sessions);
		MemoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
	}

	public void Reset()
	{
		Db.MemoryStores.Where(s => s.ProjectKey == Proj).Delete();
		using var sessions = SessionsFactory.NewEnsuredConnection(Proj);
		TestDataReset.WipeAllTables(sessions);
		using var memory = MemoryFactory.NewEnsuredConnection(Proj);
		TestDataReset.WipeAllTables(memory);
	}

	public void Dispose()
	{
		Db.Dispose();
		SessionsFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		MemoryFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}
}

public sealed class SessionDigestUsageTests : IClassFixture<SessionDigestUsageFixture>, IDisposable
{
	const string Proj = SessionDigestUsageFixture.Proj;
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly ScopedDbFactory<MemoryDb> _memoryFactory;
	readonly SessionService _sessions;
	readonly MemoryService _memory;
	readonly DuckDbSessionEpisodicIndex _episodic;
	readonly SessionTermIndex _termIndex;
	readonly SessionFullScanIndex _fullScanIndex;
	readonly ISettingsResolver _settingsResolver;
	readonly SessionSearchService _search;
	readonly MemoryUsageRecorder _usage;

	public SessionDigestUsageTests(SessionDigestUsageFixture fx)
	{
		fx.Reset();
		_db = fx.Db;
		_sessionsFactory = fx.SessionsFactory;
		_memoryFactory = fx.MemoryFactory;
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _memoryFactory), llm: null);
		_episodic = new DuckDbSessionEpisodicIndex(_sessionsFactory);
		_termIndex = new SessionTermIndex(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions);
		_fullScanIndex = new SessionFullScanIndex(_sessions);
		_settingsResolver = new SettingsResolver(new SettingsStore(_db.Factory()), new NoSecrets());
		_search = new SessionSearchService(_memory, _episodic, _termIndex, _fullScanIndex, _settingsResolver, _sessions);
		_usage = new MemoryUsageRecorder(_memoryFactory);
	}

	public void Dispose()
	{
		_episodic.Dispose();
		_usage.DisposeAsync().AsTask().GetAwaiter().GetResult();
	}

	static SessionMessageInput[] Msgs(params string[] contents) =>
		contents.Select(c => new SessionMessageInput("user", c)).ToArray();

	Task<int> Distill() =>
		new SessionDigestJob(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _memory, new EchoChat(),
				logger: null, quietPeriod: NoQuiet)
			.DrainAllAsync(CancellationToken.None);

	async Task SeedOneDistilledSession()
	{
		await _sessions.UpsertAsync(Proj, "s-0", "claude-code",
			Msgs("разговор про векторизацию индекса", "прочее"));
		(await Distill()).Should().Be(1);
	}

	Task<PetBox.Web.Mcp.Contract.SessionSearchResultView> SearchTool(string q = "векторизацию") =>
		PetBox.Web.Mcp.SessionTools.SearchAsync(ToolHttp(), ToolFlags(), _sessions, _search, _usage, Proj, q);

	// ── the fix: session_search bumps entry_usage for the digest it delivered ────────────────

	[Fact]
	public async Task SessionSearch_BumpsSurfaced_ForTheDeliveredDigest()
	{
		await SeedOneDistilledSession();

		var res = await SearchTool();
		await _usage.FlushAsync();

		res.Items.Should().ContainSingle(i => i.SessionId == "s-0" && i.Sources!.Contains("digest"));

		var u = (await _memory.GetUsageAsync(Proj, SessionDigestJob.Store))["s-0"];
		u.Surfaced.Should().Be(1, "the digest that reached the caller must count as an impression");
	}

	// ── owner decision 1 (2026-08-27): source is MACHINE, not deliberate ─────────────────────

	[Fact]
	public async Task SessionSearch_CountsSurfaced_ButNeverDeliberate()
	{
		await SeedOneDistilledSession();

		await SearchTool();
		await _usage.FlushAsync();

		var u = (await _memory.GetUsageAsync(Proj, SessionDigestJob.Store))["s-0"];
		u.Deliberate.Should().Be(0,
			"the agent searched SESSIONS, not facts — digest discovery is internal machinery and " +
			"must not inflate the deliberate signal GC trusts");
	}

	// ── acceptance criterion 3: the deliberate-filtered rollup is unchanged by this fix ──────

	[Fact]
	public async Task SessionSearch_LeavesTheDeliberateRollup_Unchanged()
	{
		await SeedOneDistilledSession();

		var before = await _memory.GetUsageAggregateAsync(Proj, SessionDigestJob.Store);
		before.DeliberatelySurfacedAtLeastOnce.Should().Be(0, "nothing surfaced this store yet");

		await SearchTool();
		await _usage.FlushAsync();

		var after = await _memory.GetUsageAggregateAsync(Proj, SessionDigestJob.Store);
		after.SurfacedAtLeastOnce.Should().Be(1, "the raw (machine-inclusive) count DOES move — that is the fix");
		after.DeliberatelySurfacedAtLeastOnce.Should().Be(before.DeliberatelySurfacedAtLeastOnce,
			"a filter down to deliberate-only traffic must read identically before and after this fix, " +
			"or the split the sibling card built would not actually separate machine noise from signal");
	}

	// ── a candidate found ONLY via term/fullscan carries no session-digests entry to credit ──

	[Fact]
	public async Task SessionSearch_NeverCreditsATermOnlyCandidate_WithNoDigestEntry()
	{
		// The declared recall floor (spec session-discovery-verbatim, mirrored from
		// SessionSearchServiceTests.VerbatimTermIndex_IsTheRecallFloor_EvenWithNoDigestStore): a
		// session found ONLY by the verbatim term leg — no digest store exists at all here, so
		// SessionSearchService synthesizes an empty MemoryEntryView for it (no session-digests row
		// exists to reach). Crediting it would write a phantom entry_usage key.
		const string Term = "уникальный-маркер-xyzzy";
		await _sessions.UpsertAsync(Proj, "s-term-only", "claude-code", Msgs($"метка {Term} в логах"));

		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse();
		(await _termIndex.DrainAllAsync(CancellationToken.None)).Should().Be(1);

		var res = await SearchTool(Term);
		await _usage.FlushAsync();

		var found = res.Items.SingleOrDefault(i => i.SessionId == "s-term-only");
		found.Should().NotBeNull("the term leg must still find it with no digest store at all");
		found!.Sources.Should().NotContain("digest");

		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse(
			"the fix must not create a session-digests store out of thin air just to record usage");
	}

	static IHttpContextAccessor ToolHttp()
	{
		var id = new System.Security.Claims.ClaimsIdentity(
			[new System.Security.Claims.Claim("project", Proj),
			 new System.Security.Claims.Claim("scopes", "tasks:read,memory:read")], "test");
		var ctx = new DefaultHttpContext
		{
			RequestServices = TestProjectCatalog.Services,
			User = new System.Security.Claims.ClaimsPrincipal(id),
		};
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags ToolFlags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}

	// Digest fake that echoes the distilled messages, so a digest carries its session's
	// distinctive tokens — same shape SessionSearchCursorTests uses.
	sealed class EchoChat : ILlmClient
	{
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			var prompt = request.Messages[^1].Content;
			var at = prompt.IndexOf("NEW MESSAGES:", StringComparison.Ordinal);
			var body = (at < 0 ? prompt : prompt[at..])
				.Replace("NEW MESSAGES:", "").Replace("[user]", "").Trim();
			var firstLine = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.FirstOrDefault() ?? "сессия";
			return Task.FromResult(new ChatResult($"Сессия: {firstLine}\n- {body.ReplaceLineEndings(" ")}",
				new ModelIdentity("fake-chat", 0), new ServedBy("fake", "fake-chat", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}
}
