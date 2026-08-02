using System.Reflection;
using LinqToDB;
using Microsoft.Extensions.Configuration;
using PetBox.Config;
using PetBox.Core.Contract;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Episodic;
using PetBox.Sessions.Search;
using PetBox.Sessions.Services;
using PetBox.Tests.Search;
using PetBox.Web.Pages.ProjectHome;
using PetBox.Web.Search;
using PetBox.Web.Settings;
using PetBox.Core.Data;

namespace PetBox.Tests.Sessions;

// The session-search OUTCOME memo (card: ui-search-render-memoized): re-opening the same search, or
// coming back to it, must replay the previous answer instead of re-running a pipeline that costs
// ~5.9s of discovery plus ~2.7s per hydrated session on real data.
//
// These run against a REAL SessionSearchMemo over a REAL HybridCache over a REAL
// SqliteDistributedCache (PoolCacheHarness), never a dictionary stub — the memo serializes, versions
// and round-trips episodic CONTENT through a database, and every one of those steps is a place an
// answer can come back subtly different from the one that went in. A stub handing the same object
// back would assert nothing about what ships.
public sealed class SessionSearchMemoTests : IDisposable
{
	readonly PoolCacheHarness _harness = new();

	public void Dispose() => _harness.Dispose();

	SessionSearchMemo Memo(TimeSpan? ttl = null) => new(_harness.Hybrid, log: null, ttl: ttl);

	// A synthetic outcome carrying real episodic CONTENT — the thing this layer stores and the pool
	// cache deliberately does not. `dataVersion` doubles as the identity a cursor binds to.
	static SessionSearchOutcome Outcome(string dataVersion, bool degraded = false) =>
		new(Distilled: true,
			Reason: null,
			Candidates: [
				new SessionSearchCandidate("s-1", "claude-code", "про векторизацию индекса",
					[new SessionEpisodicHit(7, "user", "…фрагмент про векторизацию…", 1.5, "lexical")],
					new SearchRetrievers(true, false, false, null, null, SearchRankingOutcome.ChosenRrf),
					["digest", "term"])],
			Discovery: new SearchRetrievers(true, false, degraded,
				degraded ? SearchDegradedReason.EmbedNoRoute : null, null, SearchRankingOutcome.ChosenRrf),
			PoolLimit: SessionSearchService.DiscoveryPoolLimit,
			PoolBounded: false,
			MoreInPool: true,
			DataVersion: dataVersion,
			LastPoolKey: "s-1");

	// A compute that COUNTS and hands back a different data version every time, so "the engine did not
	// run again" is proved by the answer's identity as well as by the counter.
	static Func<CancellationToken, ValueTask<SessionSearchOutcome>> Counting(StrongBox<int> calls, bool degraded = false) =>
		_ =>
		{
			calls.Value++;
			return ValueTask.FromResult(Outcome($"dv-{calls.Value}", degraded));
		};

	sealed class StrongBox<T> { public T Value = default!; }

	static SessionSearchMemo.MemoKey Key(
		string project = "proj", string query = "векторизацию", int sessions = 40,
		SearchRankingMode mode = SearchRankingMode.Speed, string? after = null) =>
		SessionSearchMemo.MemoKey.For(project, query, sessions, mode, after);

	// ── THE product claim: a repeat runs no second search, and replays the CONTENT ───────────────

	[Fact]
	public async Task RepeatOfTheSameSearch_RunsTheEngineOnce_AndReplaysTheEpisodicContent()
	{
		var memo = Memo();
		var calls = new StrongBox<int>();

		var first = await memo.GetOrComputeAsync(Key(), Counting(calls));
		var second = await memo.GetOrComputeAsync(Key(), Counting(calls));

		calls.Value.Should().Be(1, "the whole point of the card is that coming back does not search again");
		first.FromCache.Should().BeFalse();
		second.FromCache.Should().BeTrue();

		// Content, not just addresses — this is the invariant SearchPoolCache deliberately does NOT
		// offer and the reason this is a second layer.
		second.Outcome.Candidates.Should().HaveCount(1);
		second.Outcome.Candidates[0].Hits[0].Snippet.Should().Be(first.Outcome.Candidates[0].Hits[0].Snippet);
		second.Outcome.Candidates[0].Hits[0].Message.Should().Be(7);
		second.Outcome.Candidates[0].Sources.Should().Equal("digest", "term");
		second.Outcome.Candidates[0].Retrievers.Ranking.Should().Be(SearchRankingOutcome.ChosenRrf);
		second.Outcome.MoreInPool.Should().BeTrue();
		second.Outcome.PoolLimit.Should().Be(SessionSearchService.DiscoveryPoolLimit);
		second.Outcome.LastPoolKey.Should().Be("s-1");
		memo.Hits.Should().Be(1);
		memo.Misses.Should().Be(1);
	}

	// ── EQUIVALENCE: absent ≡ empty ≡ default, because the key is built from ENGINE arguments ────

	[Fact]
	public async Task AnEmptyCursor_IsTheFirstPage_NotAFourthPool()
	{
		var memo = Memo();
		var calls = new StrongBox<int>();

		Key(after: null).Fingerprint().Should().Be(Key(after: "").Fingerprint());
		Key(after: null).Fingerprint().Should().Be(Key(after: "   ").Fingerprint());

		await memo.GetOrComputeAsync(Key(after: null), Counting(calls));
		await memo.GetOrComputeAsync(Key(after: ""), Counting(calls));
		await memo.GetOrComputeAsync(Key(after: "   "), Counting(calls));

		calls.Value.Should().Be(1, "`?cursor=` is an ABSENT cursor, which is the first page");
	}

	[Fact]
	public async Task PageSizesThatTheEngineClampsTogether_ShareOneEntry()
	{
		var memo = Memo();
		var calls = new StrongBox<int>();

		// The UI offers 10/20/40/100; the engine caps hydration at MaxSessions (30). So 40 and 100 are
		// the SAME search and must not be two misses — normalization falls out of running the counts
		// through the engine's own clamp rather than out of parsing the query string.
		SessionSearchService.ClampSessions(40).Should().Be(SessionSearchService.MaxSessions);
		SessionSearchService.ClampSessions(100).Should().Be(SessionSearchService.MaxSessions);

		await memo.GetOrComputeAsync(Key(sessions: 40), Counting(calls));
		await memo.GetOrComputeAsync(Key(sessions: 100), Counting(calls));

		calls.Value.Should().Be(1);

		// …and a size the engine does NOT collapse stays its own entry, or the "equivalence" above
		// would just be a cache that ignores page size.
		await memo.GetOrComputeAsync(Key(sessions: 10), Counting(calls));
		calls.Value.Should().Be(2);
	}

	// ── ISOLATION: the expensive mistake ─────────────────────────────────────────────────────────

	[Fact]
	public async Task AnotherProject_NeverReadsThisOnesMemoizedAnswer()
	{
		var memo = Memo();
		// ONE shared counter, so the two projects' answers are distinguishable by content as well as by
		// the call count — a shared counter makes the second compute return "dv-2", and reading "dv-1"
		// back under proj-b would be the leak this test exists to catch.
		var calls = new StrongBox<int>();

		var a = await memo.GetOrComputeAsync(Key(project: "proj-a"), Counting(calls));
		var b = await memo.GetOrComputeAsync(Key(project: "proj-b"), Counting(calls));

		calls.Value.Should().Be(2, "a tenant boundary is not a cache-key nicety");
		b.FromCache.Should().BeFalse();
		a.Outcome.DataVersion.Should().Be("dv-1");
		b.Outcome.DataVersion.Should().Be("dv-2");
	}

	[Fact]
	public async Task ADifferentRankingMode_IsADifferentEntry()
	{
		var memo = Memo();
		var calls = new StrongBox<int>();

		// Ranking mode is a PER-USER preference (BrowserState.SearchRankingMode) that changes the
		// ANSWER, so one reader's Speed result must never be replayed to a reader asking for Precision.
		await memo.GetOrComputeAsync(Key(mode: SearchRankingMode.Speed), Counting(calls));
		await memo.GetOrComputeAsync(Key(mode: SearchRankingMode.Precision), Counting(calls));

		calls.Value.Should().Be(2);
	}

	[Fact]
	public async Task ADifferentCursorPosition_IsADifferentEntry()
	{
		var memo = Memo();
		var calls = new StrongBox<int>();

		// Page 2 hydrates a genuinely different slice, so sharing page 1's entry would serve the wrong
		// rows. The memo makes RETURNING cheap; it never pretends two pages are one.
		await memo.GetOrComputeAsync(Key(after: null), Counting(calls));
		await memo.GetOrComputeAsync(Key(after: "s-1"), Counting(calls));

		calls.Value.Should().Be(2);
	}

	// The MECHANICAL guard behind both isolation tests: the key carries one field per engine argument,
	// so a new argument on SearchAsync (a visibility axis, a new filter) cannot be added without this
	// failing. It is also where "there is no user axis" is pinned: SearchAsync takes no
	// ClaimsPrincipal, every leg it drives is scoped to projectKey alone, and visibility for this
	// surface is decided by the page's WorkspaceViewer policy BEFORE the search is reached — the same
	// container discipline MemorySearchScope uses. If a user-dependent argument ever appears here, this
	// test fails and the key must grow to cover it.
	[Fact]
	public void TheKeyCarriesEveryArgumentOfSearchAsync_AndNoIdentity()
	{
		var engineArgs = typeof(SessionSearchService)
			.GetMethod(nameof(SessionSearchService.SearchAsync))!
			.GetParameters()
			.Where(p => p.ParameterType != typeof(CancellationToken))
			.Select(p => p.Name!)
			.ToList();

		var keyFields = typeof(SessionSearchMemo.MemoKey)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => p.Name)
			.ToList();

		keyFields.Select(f => f.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)
			.Should().Equal(engineArgs.Select(a => a.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal),
				"the memo key must cover every input that can change the answer — no more, no less");

		typeof(SessionSearchService).GetMethod(nameof(SessionSearchService.SearchAsync))!
			.GetParameters().Should().NotContain(p => p.ParameterType.Name.Contains("ClaimsPrincipal", StringComparison.Ordinal),
				"session search has no per-user visibility axis; if it grows one it belongs in the key");
	}

	// ── FRESHNESS: TTL is the whole mechanism, so it has to actually expire ───────────────────────

	[Fact]
	public async Task PastTheTtl_TheStaleAnswerIsNotServed()
	{
		var memo = Memo(ttl: TimeSpan.FromMilliseconds(500));
		var calls = new StrongBox<int>();

		var first = await memo.GetOrComputeAsync(Key(), Counting(calls));
		first.Outcome.DataVersion.Should().Be("dv-1");

		// Inside the window: replayed.
		(await memo.GetOrComputeAsync(Key(), Counting(calls))).Outcome.DataVersion.Should().Be("dv-1");
		calls.Value.Should().Be(1);

		await Task.Delay(TimeSpan.FromMilliseconds(1500));

		var afterTtl = await memo.GetOrComputeAsync(Key(), Counting(calls));

		calls.Value.Should().Be(2, "TTL is the ONLY freshness mechanism here — if it does not expire, nothing does");
		afterTtl.FromCache.Should().BeFalse();
		afterTtl.Outcome.DataVersion.Should().Be("dv-2", "a session written after the search must become visible once the window closes");
	}

	[Fact]
	public async Task ADegradedAnswer_IsNeverStored()
	{
		var memo = Memo();
		var calls = new StrongBox<int>();

		// A degraded discovery (a dead embed route, a failed FTS hydration) must not be pinned for the
		// whole TTL — this layer has no data version to expire it early, so every repeat would keep
		// hitting an outage that has already healed.
		await memo.GetOrComputeAsync(Key(), Counting(calls, degraded: true));
		await memo.GetOrComputeAsync(Key(), Counting(calls, degraded: true));

		calls.Value.Should().Be(2);
		memo.Stores.Should().Be(0);
	}

	// ── CURSOR SEMANTICS (owner's point 4): returning to a page keeps its pool identity ───────────

	[Fact]
	public async Task ReturningToAPageInsideTheTtl_KeepsThePoolIdentityItsCursorWasMintedFrom()
	{
		var memo = Memo();
		var calls = new StrongBox<int>();

		var page = await memo.GetOrComputeAsync(Key(), Counting(calls));
		var fingerprint = KeysetCursor.FingerprintOf("sessions-ui-search", "proj", "векторизацию", null);
		var token = new KeysetCursor(fingerprint, "", "s-1", "proj", page.Outcome.DataVersion ?? "");

		// Coming back replays the SAME outcome, so the order commitment the page asserts still holds and
		// CursorWasReset stays false where a recompute might have tripped it.
		var back = await memo.GetOrComputeAsync(Key(), Counting(calls));
		var assertReplayed = () => token.AssertPoolOrder(back.Outcome.DataVersion ?? "", "sessions-ui-search");
		assertReplayed.Should().NotThrow();

		// And the check is not vacuous: a genuine recompute yields a different order identity, which the
		// SAME assertion refuses. That is the behaviour the memo removes for a RETURN, and deliberately
		// keeps for a real data move.
		calls.Value.Should().Be(1);
		var recomputed = Outcome("dv-moved");
		var assertMoved = () => token.AssertPoolOrder(recomputed.DataVersion ?? "", "sessions-ui-search");
		assertMoved.Should().Throw<ArgumentException>();
	}

	[Fact]
	public async Task AFailingSearch_IsNeverMemoized()
	{
		var memo = Memo();
		var calls = new StrongBox<int>();

		// SessionSearchService throws when a cursor names a session that fell out of the pool. The page
		// catches it and restarts from the top — which only works if the throw is not stored and not
		// swallowed by the memo.
		var boom = async () => await memo.GetOrComputeAsync(Key(after: "gone"),
			_ => throw new ArgumentException("session_search: the session this cursor names is no longer in the discovery pool"));
		await boom.Should().ThrowAsync<ArgumentException>();

		var after = await memo.GetOrComputeAsync(Key(after: "gone"), Counting(calls));
		after.FromCache.Should().BeFalse();
		calls.Value.Should().Be(1);
	}
}

// The PAGE's half of the contract: that `agent`, `sortBy`/`sortDesc` and `size` never reach the
// engine, so the four URL shapes the card names are ONE memo entry rather than four misses.
//
// Driven through SessionsModel.OnGetAsync against real stores rather than by re-deriving the key in
// the test — the claim is about what the PAGE hands the engine, and a test that built the key itself
// would pass even if OnGetAsync started passing the agent filter down.
public sealed class SessionsPageMemoFixture : IDisposable
{
	public const string Proj = "proj";
	public const string Ws = "ws";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<SessionsDb> SessionsFactory { get; }
	public ScopedDbFactory<MemoryDb> MemoryFactory { get; }

	public SessionsPageMemoFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessmemo-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		Db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		Db.Insert(new Project { Key = Proj, WorkspaceKey = Ws, Name = "P", Description = "" });
		SessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), TestSchema.Sessions);
		MemoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
	}

	// Per-test DATA isolation over a shared per-class host: the digest job only distills sessions whose
	// version moved past its cursor, so a second test reusing the same ids would seed nothing and assert
	// against an empty discovery pool.
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

public sealed class SessionsPageMemoTests : IClassFixture<SessionsPageMemoFixture>, IDisposable
{
	const string Proj = SessionsPageMemoFixture.Proj;
	const string Ws = SessionsPageMemoFixture.Ws;
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly SessionService _sessions;
	readonly MemoryService _memory;
	readonly DuckDbSessionEpisodicIndex _episodic;
	readonly SessionSearchService _search;
	readonly PoolCacheHarness _harness = new();
	readonly SessionSearchMemo _memo;

	public SessionsPageMemoTests(SessionsPageMemoFixture fx)
	{
		fx.Reset();
		_db = fx.Db;
		_sessionsFactory = fx.SessionsFactory;
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		_memory = new MemoryService(new MemoryStore(_db.Factory(), fx.MemoryFactory), llm: null);
		_episodic = new DuckDbSessionEpisodicIndex(_sessionsFactory);
		_search = new SessionSearchService(_memory, _episodic,
			new SessionTermIndex(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions),
			new SessionFullScanIndex(_sessions),
			new SettingsResolver(new SettingsStore(_db.Factory()), new NoSecrets()),
			_sessions);
		_memo = new SessionSearchMemo(_harness.Hybrid);
	}

	public void Dispose()
	{
		_episodic.Dispose();
		_harness.Dispose();
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	SessionsModel Page() =>
		// uiState null → the page's own Speed default, so the ranking mode is deterministic here.
		new(_db.Factory().Projects(), Flags(), new SessionStore(_sessionsFactory), _search, uiState: null, memo: _memo)
		{
			WorkspaceKey = Ws,
			ProjectKey = Proj,
		};

	async Task SeedAsync()
	{
		for (var i = 0; i < 3; i++)
			await _sessions.UpsertAsync(Proj, $"s-{i}", "claude-code",
				[new SessionMessageInput("user", $"разговор {i} про векторизацию индекса")]);
		var distilled = await new SessionDigestJob(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions,
			_memory, new EchoChat(), logger: null, quietPeriod: NoQuiet).DrainAllAsync(CancellationToken.None);
		distilled.Should().Be(3, "an empty discovery pool would make every assertion below vacuous");
	}

	[Fact]
	public async Task TheFourUrlShapesTheCardNames_AreOneSearch_NotFourMisses()
	{
		await SeedAsync();

		// 1. the bare query
		var bare = Page();
		bare.Query = "векторизацию";
		await bare.OnGetAsync(CancellationToken.None);

		bare.SearchResults.Should().NotBeEmpty("a vacuous pool would prove nothing about the memo");
		_memo.Misses.Should().Be(1);
		_memo.Stores.Should().Be(1, "a degraded discovery is never stored — if this is 0 the rest is vacuous");

		// 2. `?agent=` — an EMPTY filter is an ABSENT one, and the agent filter is applied to the
		//    already-fetched pool, so it never reaches the engine at all.
		var emptyAgent = Page();
		emptyAgent.Query = "векторизацию";
		emptyAgent.Agent = "";
		await emptyAgent.OnGetAsync(CancellationToken.None);

		// 3. `?sortBy=updated&sortDesc=true` — spelling out the DEFAULTS (EffectiveSortBy /
		//    EffectiveSortDesc). Sort reorders the fetched page; it never re-asks the engine.
		var explicitDefaults = Page();
		explicitDefaults.Query = "векторизацию";
		explicitDefaults.SortBy = "updated";
		explicitDefaults.SortDesc = true;
		await explicitDefaults.OnGetAsync(CancellationToken.None);

		// 4. `?size=100` — a different page size that the engine's hydration cap collapses onto the
		//    same 30 sessions the default 40 already asked for.
		var biggerSize = Page();
		biggerSize.Query = "векторизацию";
		biggerSize.Size = 100;
		await biggerSize.OnGetAsync(CancellationToken.None);

		_memo.Misses.Should().Be(1, "all four URL shapes are the SAME search");
		_memo.Hits.Should().Be(3);

		// Faithful replay, not merely a cheap one.
		emptyAgent.SearchResults.Select(c => c.SessionId)
			.Should().Equal(bare.SearchResults.Select(c => c.SessionId));
		biggerSize.SearchResults.Select(c => c.SessionId)
			.Should().Equal(bare.SearchResults.Select(c => c.SessionId));

		// `?sortBy=updated` is the same SEARCH but not necessarily the same RENDERED ORDER, and the
		// difference is real rather than a rounding of this test: OnGetAsync only reorders when `sortBy`
		// is PRESENT (`if (!string.IsNullOrWhiteSpace(SortBy))`), so an absent one leaves the discovery
		// order untouched while an explicit "updated" applies the header sort — even though
		// EffectiveSortBy reports "updated" for both. That is a presentation difference over one cached
		// pool, which is exactly the split the card asked for ("cache the SEARCH, not the page"), so the
		// assertion is on the selected SET.
		explicitDefaults.SearchResults.Select(c => c.SessionId)
			.Should().BeEquivalentTo(bare.SearchResults.Select(c => c.SessionId));

		// Query-string parameter ORDER is not observable here on purpose: model binding has already
		// resolved the values by the time OnGetAsync runs, so order-independence is structural rather
		// than something a normalization pass has to earn.
	}

	[Fact]
	public async Task AnAgentFilterStillNarrows_ThoughItNeverReachesTheEngine()
	{
		await SeedAsync();

		var all = Page();
		all.Query = "векторизацию";
		await all.OnGetAsync(CancellationToken.None);
		all.SearchResults.Should().NotBeEmpty();

		// The memo must not turn a post-filter into a no-op: same entry, different rendered rows.
		var filtered = Page();
		filtered.Query = "векторизацию";
		filtered.Agent = "nobody-by-this-name";
		await filtered.OnGetAsync(CancellationToken.None);

		filtered.SearchResults.Should().BeEmpty("the agent filter still applies — it just applies AFTER the memo");
		_memo.Misses.Should().Be(1);
		_memo.Hits.Should().Be(1);
	}

	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}

	// Digest fake that echoes the distilled messages, so a digest carries its session's distinctive
	// tokens — the shape the real facts-distillation prompt asks for. Local copy: the original is
	// private to SessionSearchServiceTests (same precedent as SessionSearchCursorTests).
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
