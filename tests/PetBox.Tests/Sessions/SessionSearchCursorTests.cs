using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Config;
using PetBox.Core.Features;
using PetBox.Core.Data;
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

// session_search PAGINATION (work/search-results-pageable, spec result-set-pageable).
//
// What is paged here is the DISCOVERY ORDER — the sessions that will be SHOWN — not the digests. A
// digest is how a session was found, several can point at one session, and a caller never navigates
// them. So the pool addresses session ids, and each page hydrates only its own slice: paging must never
// turn a sublinear discovery into a full archive scan.
//
// Sessions have NO cross-encoder rerank (SessionSearchService takes only SearchOrderingPolicies), so
// there is no "one rerank per pool" claim to make here and nothing in this contract offers a ranking
// mode. What must still hold is everything else: pages concatenate to the unpaged answer, a repeat walk
// reproduces it, the stop reason is stated in the SAME words the other surfaces use, and a discovery
// order that moved under an in-flight cursor is a refusal rather than a silent restart.
//
// Shared per-class host (work share-fixtures-across-per-test-classes, wave 2): the migrated core +
// sessions + memory DB files are the expensive part of this class's constructor, not the thin service
// wrappers over them, so the fixture owns the FILES and the test class rebuilds the (cheap) services
// fresh per test. Per-test DATA isolation is TestDataReset.WipeAllTables over both per-project files —
// not TestDirs.ResetDbFile, which costs more than a fresh templated copy (see TestDataReset). The
// episodic index (_episodic) is deliberately rebuilt per test rather than shared: its hydration cache
// keys on (sessionId, session Version), and a reset test reusing the same session ids would otherwise
// risk a stale cache hit against a same-numbered version from the PREVIOUS test's data.
public sealed class SessionSearchCursorFixture : IDisposable
{
	public const string Proj = "proj";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<SessionsDb> SessionsFactory { get; }
	public ScopedDbFactory<MemoryDb> MemoryFactory { get; }

	public SessionSearchCursorFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sesscursor-" + Guid.NewGuid().ToString("N"));
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

	// Wipe both per-project files, plus the memory store CATALOG (MemoryStoreMeta lives in core,
	// like TaskBoards — MemoryStore.CreateAsync throws "already exists" against a leftover row).
	// The core Project row itself is never mutated by these tests, so it (and the migrated schema
	// everywhere) is left alone.
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

public sealed class SessionSearchCursorTests : IClassFixture<SessionSearchCursorFixture>, IDisposable
{
	const string Proj = SessionSearchCursorFixture.Proj;
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly SessionService _sessions;
	readonly MemoryService _memory;
	readonly DuckDbSessionEpisodicIndex _episodic;
	readonly SessionTermIndex _termIndex;
	readonly SessionFullScanIndex _fullScanIndex;
	readonly ISettingsResolver _settingsResolver;
	readonly SessionSearchService _search;

	public SessionSearchCursorTests(SessionSearchCursorFixture fx)
	{
		fx.Reset();
		_db = fx.Db;
		_sessionsFactory = fx.SessionsFactory;
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		_memory = new MemoryService(new MemoryStore(_db.Factory(), fx.MemoryFactory), llm: null);
		_episodic = new DuckDbSessionEpisodicIndex(_sessionsFactory);
		_termIndex = new SessionTermIndex(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions);
		_fullScanIndex = new SessionFullScanIndex(_sessions);
		_settingsResolver = new SettingsResolver(new SettingsStore(_db.Factory()), new NoSecrets());
		_search = new SessionSearchService(_memory, _episodic, _termIndex, _fullScanIndex, _settingsResolver, _sessions);
	}

	public void Dispose() => _episodic.Dispose();

	static SessionMessageInput[] Msgs(params string[] contents) =>
		contents.Select(c => new SessionMessageInput("user", c)).ToArray();

	Task<int> Distill() =>
		new SessionDigestJob(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _memory, new EchoChat(),
				logger: null, quietPeriod: NoQuiet)
			.DrainAllAsync(CancellationToken.None);

	// Six discoverable sessions. Distillation is part of the SEED, not an extra: without a digest store
	// the discovery pool is empty here, and a walk over an empty pool would pass every concatenation
	// assertion vacuously — proving nothing while looking green.
	async Task SeedSix()
	{
		for (var i = 0; i < 6; i++)
			await _sessions.UpsertAsync(Proj, $"s-{i}", "claude-code",
				Msgs($"разговор {i} про векторизацию индекса", "прочее"));
		(await Distill()).Should().Be(6);
	}

	// Walk the discovery pool page by page, returning every session id in page order.
	async Task<List<string>> WalkAsync(int pageSize)
	{
		var ids = new List<string>();
		string? after = null;
		for (var guard = 0; guard < 100; guard++)
		{
			var res = await _search.SearchAsync(Proj, "векторизацию", sessions: pageSize, afterSessionId: after);
			ids.AddRange(res.Candidates.Select(c => c.SessionId));
			if (!res.MoreInPool) return ids;
			after = res.LastPoolKey;
		}
		throw new InvalidOperationException("page walk did not terminate — MoreInPool never went false");
	}

	// ── THE invariant: pages == the whole thing ──────────────────────────────────────────────

	[Fact]
	public async Task PageWalk_ConcatenatesToTheUnpagedDiscovery()
	{
		await SeedSix();

		var whole = (await _search.SearchAsync(Proj, "векторизацию", sessions: 30))
			.Candidates.Select(c => c.SessionId).ToList();
		whole.Should().HaveCount(6);

		var paged = await WalkAsync(pageSize: 2);

		paged.Should().Equal(whole, "paging must change presentation only — never selection or order");
		paged.Should().OnlyHaveUniqueItems("a keyset seek re-serves nothing");
	}

	[Fact]
	public async Task PageWalk_PageSizeOfOne_CoversThePoolWithoutHoles()
	{
		await SeedSix();

		var whole = (await _search.SearchAsync(Proj, "векторизацию", sessions: 30))
			.Candidates.Select(c => c.SessionId).ToList();

		(await WalkAsync(pageSize: 1)).Should().Equal(whole);
	}

	[Fact]
	public async Task SecondWalk_OverUnchangedData_ReproducesTheFirstWalkExactly()
	{
		await SeedSix();

		var first = await WalkAsync(pageSize: 2);
		var second = await WalkAsync(pageSize: 2);

		second.Should().Equal(first);
	}

	[Fact]
	public async Task EachPage_HydratesOnlyItsOwnSlice_NotTheWholePool()
	{
		// The cost guarantee: a page of 2 returns 2 candidates even though the pool holds 6. If paging
		// ever hydrated the pool to find its slice, discovery would stop being sublinear to the archive.
		await SeedSix();

		var page = await _search.SearchAsync(Proj, "векторизацию", sessions: 2);

		page.Candidates.Should().HaveCount(2);
		page.MoreInPool.Should().BeTrue();
	}

	// ── the pool addresses what is SHOWN, and declares its depth ─────────────────────────────

	[Fact]
	public async Task ThePool_AddressesSessions_NotDigests()
	{
		// Two sessions, distilled — the pool must be keyed by session id (what a caller navigates),
		// which is what makes a resume point meaningful across pages.
		await SeedSix();

		var walked = await WalkAsync(pageSize: 2);

		walked.Should().OnlyHaveUniqueItems("one row per SESSION — a session found by several digests is still one row");
		walked.Should().BeSubsetOf(Enumerable.Range(0, 6).Select(i => $"s-{i}"));
	}

	[Fact]
	public async Task Response_DeclaresTheDiscoveryDepth_AndWhetherItWasReached()
	{
		await SeedSix();

		var res = await _search.SearchAsync(Proj, "векторизацию", sessions: 2);

		res.PoolLimit.Should().Be(SessionSearchService.DiscoveryPoolLimit);
		res.PoolBounded.Should().BeFalse("six sessions is nowhere near the discovery depth — this is a real exhaustion");
	}

	[Fact]
	public async Task LastPage_ReportsNoMore()
	{
		await SeedSix();

		var res = await _search.SearchAsync(Proj, "векторизацию", sessions: 30);

		res.MoreInPool.Should().BeFalse();
		res.PoolBounded.Should().BeFalse();
	}

	// ── the deliberate refusals ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task DataVersion_Changes_WhenANewSessionJoinsTheDiscoveryOrder()
	{
		// The stamp IS the discovery order, so a newcomer that enters the pool must move it — that is
		// what makes the adapter's cursor refuse an in-flight walk instead of splicing two orderings.
		await SeedSix();
		var before = (await _search.SearchAsync(Proj, "векторизацию", sessions: 2)).DataVersion;

		await _sessions.UpsertAsync(Proj, "s-new", "claude-code", Msgs("ещё один разговор про векторизацию"));
		await Distill();
		var after = (await _search.SearchAsync(Proj, "векторизацию", sessions: 2)).DataVersion;

		before.Should().NotBeNull();
		after.Should().NotBe(before);
	}

	[Fact]
	public async Task DataVersion_IsStable_WhenNothingChanged()
	{
		await SeedSix();

		var a = (await _search.SearchAsync(Proj, "векторизацию", sessions: 2)).DataVersion;
		var b = (await _search.SearchAsync(Proj, "векторизацию", sessions: 2)).DataVersion;

		b.Should().Be(a, "a stamp that drifted on its own would refuse every second page for no reason");
	}

	[Fact]
	public async Task ResumePoint_ThatLeftThePool_IsRefused_NotSilentlyRestarted()
	{
		// Restarting at the top would hand back a page that looks like a continuation and is not — the
		// exact failure the keyset design exists to prevent.
		await SeedSix();

		var act = () => _search.SearchAsync(Proj, "векторизацию", sessions: 2, afterSessionId: "s-never-existed");

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*no longer in the discovery pool*");
	}

	// ── B3: a page cut by the response budget must not strand the rows it cut ────────────────

	[Fact]
	public async Task B3_WhenTheBudgetCutsThePage_TheWalkStillDeliversTheCutRows()
	{
		// `nextCursor` used to encode the last candidate the page CONSIDERED, while `kept` could be a
		// budget-trimmed prefix of it. Page 2 then resumed past the end of the slice and the trimmed
		// candidates were never delivered — by ANY page. tasks_search and memory_search both resume from
		// the last row actually SENT; sessions must too.
		//
		// Fat transcripts + bodyLen:-1 (full raw messages) are what make the budget, not `sessions`, the
		// thing that cuts the page.
		var fat = new string('я', 4000);
		for (var i = 0; i < 12; i++)
			await _sessions.UpsertAsync(Proj, $"big-{i:d2}", "claude-code",
				Msgs($"разговор {i} про векторизацию индекса {fat}"));
		(await Distill()).Should().Be(12);

		var first = await SearchTool(sessions: 30, bodyLen: -1);
		first.Omitted.Should().NotBeNull().And.BeGreaterThan(0, "this test is only meaningful if the budget cut rows");
		first.NextCursor.Should().NotBeNull("a cut page must hand back a way to reach what it cut");

		var seen = first.Items.Select(i => i.SessionId).ToList();
		string? cursor = first.NextCursor;
		for (var guard = 0; guard < 40 && cursor is not null; guard++)
		{
			var page = await SearchTool(sessions: 30, bodyLen: -1, cursor: cursor);
			seen.AddRange(page.Items.Select(i => i.SessionId));
			cursor = page.NextCursor;
		}

		seen.Should().OnlyHaveUniqueItems("resuming from the last SENT row re-serves nothing");
		seen.Should().BeEquivalentTo(Enumerable.Range(0, 12).Select(i => $"big-{i:d2}"),
			"every discovered session must reach the caller on some page");
	}

	Task<PetBox.Web.Mcp.Contract.SessionSearchResultView> SearchTool(
		int sessions = 0, int? bodyLen = null, string? cursor = null) =>
		PetBox.Web.Mcp.SessionTools.SearchAsync(ToolHttp(), ToolFlags(), _sessions, _search, Proj,
			"векторизацию", sessions, 0, false, bodyLen, cursor);

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

	// ── fixtures (local copies: the originals are private to SessionSearchServiceTests) ──────

	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}

	// Digest fake that echoes the distilled messages, so a digest carries its session's distinctive
	// tokens — the shape the real facts-distillation prompt asks for.
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
