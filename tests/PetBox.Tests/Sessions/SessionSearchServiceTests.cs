using LinqToDB;
using PetBox.Config;
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

// The full two-stage pipeline wired end-to-end in-process: sessions are pushed →
// SessionDigestJob distills digests into memory → SessionSearchService discovers the
// right session through the digest store and drills inside it through the episodic
// index, returning message-ordinal provenance.
public sealed class SessionSearchServiceTests : IDisposable
{
	const string Proj = "proj";
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly string _dir;
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

	public SessionSearchServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sesssearch-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_sessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);
		_memoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		_memory = new MemoryService(new MemoryStore(_db, _memoryFactory), llm: null);
		_episodic = new DuckDbSessionEpisodicIndex(_sessionsFactory);
		_termIndex = new SessionTermIndex(_sessionsFactory, new ProjectCatalog(_db), _sessions);
		_fullScanIndex = new SessionFullScanIndex(_sessions);
		_settingsResolver = new SettingsResolver(_db, new NoSecrets());
		_search = new SessionSearchService(_memory, _episodic, _termIndex, _fullScanIndex, _settingsResolver, _sessions);
	}

	public void Dispose()
	{
		_episodic.Dispose();
		_db.Dispose();
		_sessionsFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_memoryFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static SessionMessageInput[] Msgs(params string[] contents) =>
		contents.Select(c => new SessionMessageInput("user", c)).ToArray();

	Task<int> Distill() =>
		new SessionDigestJob(new ProjectCatalog(_db), _sessions, _memory, new EchoChat(), logger: null, quietPeriod: NoQuiet)
			.DrainAllAsync(CancellationToken.None);

	[Fact]
	public async Task TwoStage_FindsTheRightSession_AndTheMessageInsideIt()
	{
		await _sessions.UpsertAsync(Proj, "s-vec", "claude-code",
			Msgs("обсуждали план на неделю", "мы запустили векторизацию на проде в ci.300"));
		await _sessions.UpsertAsync(Proj, "s-ui", "claude-code",
			Msgs("правили вёрстку дашборда и тёмную тему"));
		(await Distill()).Should().Be(2);

		var res = await _search.SearchAsync(Proj, "запустили векторизацию");

		res.Distilled.Should().BeTrue();
		res.Reason.Should().BeNull();                          // distilled → no reason code
		res.Discovery.Lexical.Should().BeTrue();
		res.Candidates.Should().NotBeEmpty();
		var top = res.Candidates[0];
		top.SessionId.Should().Be("s-vec");                    // stage 1 — discovered via the digest
		top.Hits.Should().NotBeEmpty();
		top.Hits[0].Message.Should().Be(2);                    // stage 2 — the verbatim message inside
		top.Hits[0].Snippet.Should().Contain("векторизацию");
		top.Retrievers.Lexical.Should().BeTrue();
	}

	[Fact]
	public async Task NoDigestStoreYet_SaysNotDistilled_InsteadOfFailing()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("что-то"));

		var res = await _search.SearchAsync(Proj, "что-то");

		res.Distilled.Should().BeFalse();
		res.Reason.Should().Be("no-digest-store");             // structural signal, not a bare bool
		res.Candidates.Should().BeEmpty();
	}

	[Fact]
	public async Task SessionDeletedAfterDistillation_StaleDigestCandidateIsSkipped()
	{
		await _sessions.UpsertAsync(Proj, "s-gone", "claude-code", Msgs("уникальный термин шарманка"));
		(await Distill()).Should().Be(1);
		await _sessions.DeleteAsync(Proj, "s-gone");

		var res = await _search.SearchAsync(Proj, "шарманка");

		res.Distilled.Should().BeTrue();
		res.Candidates.Should().BeEmpty(); // discovered but unhydratable — skipped, not an error
	}

	// ---- W1 acceptance test: verbatim term-index recall with the digest leg OFF (spec
	// session-discovery-verbatim) ----

	[Fact]
	public async Task VerbatimTermIndex_FindsSessionByBodyTermTheDigestDropped()
	{
		// FixedDigestChat's digest names "topic A" only, regardless of the transcript — it
		// models a real LLM summary that judged the identifier non-essential and dropped it.
		// Query TermB against the digest store ALONE (the pre-existing behavior) must find
		// NOTHING: that is the OFF-recall baseline the verbatim term leg fixes.
		const string TermB = "xk9917cafeface";
		await _sessions.UpsertAsync(Proj, "s-verbatim", "claude-code",
			Msgs($"обсуждали тему А, отдельно всплыл идентификатор {TermB} в логах ошибки"));

		var distilled = await new SessionDigestJob(new ProjectCatalog(_db), _sessions, _memory, new FixedDigestChat(),
			logger: null, quietPeriod: NoQuiet).DrainAllAsync(CancellationToken.None);
		distilled.Should().Be(1);

		var digestOnly = await _memory.SearchAsync(Proj, SessionDigestJob.Store, TermB, type: null);
		digestOnly.Hits.Should().BeEmpty("the digest never mentions the verbatim term — the OFF baseline");

		(await _termIndex.DrainAllAsync(CancellationToken.None)).Should().Be(1); // backfill the term index

		var res = await _search.SearchAsync(Proj, TermB);

		res.Distilled.Should().BeTrue();
		res.Candidates.Select(c => c.SessionId).Should().Contain("s-verbatim");
		var hit = res.Candidates.Single(c => c.SessionId == "s-verbatim");
		hit.Sources.Should().Contain("term");
		hit.Sources.Should().NotContain("digest"); // the digest search alone never surfaced it
	}

	[Fact]
	public async Task VerbatimTermIndex_IsTheRecallFloor_EvenWithNoDigestStore()
	{
		// The DECLARED lower bound of recall (spec session-discovery-verbatim): the term leg must
		// answer even when distillation has NEVER run and there is no digest store at all. Only a
		// session push + a term-index drain — no SessionDigestJob anywhere in this test.
		const string Term = "шарманкаzz42";
		await _sessions.UpsertAsync(Proj, "s-nodigest", "claude-code",
			Msgs($"единственная зацепка — {Term} в трейсе, дистилляция ещё не прогонялась"));

		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse(); // no digest store
		(await _termIndex.DrainAllAsync(CancellationToken.None)).Should().Be(1);            // term index populated

		var res = await _search.SearchAsync(Proj, Term);

		res.Distilled.Should().BeFalse();               // honest informational signal…
		res.Reason.Should().Be("no-digest-store");      // …but NOT a reason to return empty
		res.Candidates.Select(c => c.SessionId).Should().Contain("s-nodigest");
		var hit = res.Candidates.Single(c => c.SessionId == "s-nodigest");
		hit.Sources.Should().Equal("term");             // the term leg alone carried it
		hit.Agent.Should().Be("claude-code");           // agent recovered from the session header
	}

	// ---- W2 acceptance tests: full-scan opt-in (spec session-fullscan-optin) ----

	// A substring sitting INSIDE a longer token: term-FTS (whole-token prefix matching)
	// structurally cannot find it — the indexed token is "errorcode{X}trailing", which does
	// not START WITH the query token. Only a raw substring scan can, which is exactly the
	// escape hatch this leg exists for.
	const string FullScanSubstring = "XK77CAFE";

	async Task SeedFullScanTargetAsync()
	{
		// A baseline session gives the project a digest store (required to clear the
		// SearchAsync "no digest store" guard) without ever mentioning the substring, and is
		// distilled BEFORE the target session exists so the target is never picked up by it.
		await _sessions.UpsertAsync(Proj, "s-baseline", "claude-code",
			Msgs("обсуждали общие вопросы производительности системы без конкретных деталей"));
		await Distill();

		await _sessions.UpsertAsync(Proj, "s-fullscan-only", "claude-code",
			Msgs($"залогирован код ошибки errorcode{FullScanSubstring}trailing без дополнительных деталей"));
	}

	[Fact]
	public async Task FullScan_NotRequested_NeverRunsAutomatically()
	{
		await SeedFullScanTargetAsync();

		var res = await _search.SearchAsync(Proj, FullScanSubstring); // fullScan defaults to false

		res.FullScanRequested.Should().BeNull();
		res.FullScanRan.Should().BeNull();
		res.Candidates.Select(c => c.SessionId).Should().NotContain("s-fullscan-only");
	}

	[Fact]
	public async Task FullScan_RequestedButNotAllowed_DoesNotRun()
	{
		await SeedFullScanTargetAsync();
		// Neither switch was ever turned on — both default OFF.

		var res = await _search.SearchAsync(Proj, FullScanSubstring, fullScan: true);

		res.FullScanRequested.Should().BeTrue();
		res.FullScanRan.Should().BeFalse();
		res.FullScanReason.Should().Be("not-allowed");
		res.Candidates.Select(c => c.SessionId).Should().NotContain("s-fullscan-only");
	}

	[Fact]
	public async Task FullScan_SystemSwitchAlone_StillDenied_AndSemantics()
	{
		await SeedFullScanTargetAsync();
		await _settingsResolver.SetAsync(Scope.System, "$",
			new SessionFullScanSettings { SystemEnabled = true }, new SessionFullScanSettings(), updatedBy: null);
		// Project switch left OFF.

		var res = await _search.SearchAsync(Proj, FullScanSubstring, fullScan: true);

		res.FullScanRequested.Should().BeTrue();
		res.FullScanRan.Should().BeFalse("the system switch alone is not enough — AND semantics");
		res.FullScanReason.Should().Be("not-allowed");
	}

	[Fact]
	public async Task FullScan_SystemAndProjectAllowed_RunsAndFindsBySubstring()
	{
		await SeedFullScanTargetAsync();
		await _settingsResolver.SetAsync(Scope.System, "$",
			new SessionFullScanSettings { SystemEnabled = true }, new SessionFullScanSettings(), updatedBy: null);
		await _settingsResolver.SetAsync(Scope.Project, Proj,
			new SessionFullScanSettings { ProjectEnabled = true }, new SessionFullScanSettings(), updatedBy: null);

		var res = await _search.SearchAsync(Proj, FullScanSubstring, fullScan: true);

		res.FullScanRequested.Should().BeTrue();
		res.FullScanRan.Should().BeTrue();
		res.FullScanReason.Should().BeNull();
		res.Candidates.Select(c => c.SessionId).Should().Contain("s-fullscan-only");
		var hit = res.Candidates.Single(c => c.SessionId == "s-fullscan-only");
		hit.Sources.Should().Contain("fullscan");
	}

	// No-op secret encryptor: the settings exercised here have no [Setting(IsSecret=true)]
	// properties, so encryption is never reached — only present to satisfy the constructor.
	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}

	// Chat fake whose digest is a FIXED summary naming only "topic A" — it never echoes the
	// transcript, so any distinctive term in the body is guaranteed absent from the digest
	// (models a real LLM dropping a detail it judged non-essential to the summary).
	sealed class FixedDigestChat : ILlmClient
	{
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			Task.FromResult(new ChatResult("Сессия про тему А\n- обсуждали тему А, ничего примечательного",
				new ModelIdentity("fake-chat", 0), new ServedBy("fake", "fake-chat", 1, Degraded: false)));

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}

	// Chat fake whose digest echoes the distilled messages, so the digest carries the
	// session's distinctive tokens (what the real facts-distillation prompt asks for).
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
