using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Config;
using PetBox.Core.Data;
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
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Tests.Sessions;

// work/session-search-cursor-invalidates-immediately — session_search handed out a `nextCursor` that
// its own NEXT call refused, every time, so page 2 did not exist.
//
// WHY THE EXISTING CURSOR SUITE COULD NOT SEE IT. SessionSearchCursorTests builds MemoryService with
// `llm: null`. No LLM route means no cross-encoder, and the digest discovery leg — which DOES rerank
// (spec: search-rerank-for-sessions) — silently degrades to plain RRF, which is perfectly order-stable.
// So that whole class walks a deterministic pool and stays green on code where every real page 2 threw.
// The defect lives entirely in the half of the pipeline those tests switch off. EVERY test here is built
// with a reranker wired, and that is the point of the file.
//
// WHAT THE FAKE REPRODUCES, and why this shape. Two back-to-back identical `llm_rerank` calls against the
// live route (qwen3-rerank-0.6b, endpoint `home`, degraded:false, 8 documents) came back in DIFFERENT
// orders — [0:0.9965, 1:0.8117, 3:0.7249] then [0:0.9964, 3:0.7931, 1:0.7892]. ADJACENT RANKS traded
// places; this is not float noise in the last bits, it is a different list. AdjacentSwapReranker below
// does exactly that and nothing more: same documents, same scores, positions 1 and 2 swapped on every
// other call. Since the discovery-order stamp hashes address AND score, that is enough to move it — which
// is precisely why an uncached page 2 could never match the token page 1 issued.
//
// THE FIX under test is that the ranked discovery pool is now MATERIALIZED in SearchPoolCache, the same
// cache memory and tasks page against, so page 2 reads the order page 1 was issued against instead of
// asking a non-deterministic route to reproduce it. The guard itself is untouched, and two tests here
// exist to prove that: WithoutThePoolCache_... (the pre-fix path, still refused) and
// WhenThePoolIsGone_... (a cache miss on a route that really did reorder — still refused).
public sealed class SessionSearchPoolCacheTests : IDisposable
{
	const string Proj = "proj";
	const string Query = "векторизацию";
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly ScopedDbFactory<MemoryDb> _memoryFactory;
	readonly SessionService _sessions;
	readonly AdjacentSwapReranker _digestLlm;
	readonly MemoryService _memory;
	readonly DuckDbSessionEpisodicIndex _episodic;
	readonly SessionTermIndex _termIndex;
	readonly SessionFullScanIndex _fullScanIndex;
	readonly ISettingsResolver _settingsResolver;
	readonly PoolCacheHarness _pools;
	readonly MemoryUsageRecorder _usage;

	// The service as it now SHIPS: the shared pool cache wired in.
	readonly SessionSearchService _search;
	// The service exactly as it was BEFORE this card: no pool cache at all. SearchPoolCache.Disabled runs
	// every computation and stores nothing, which is byte-for-byte the old behaviour — so this is not a
	// simulation of the defect, it is the defect's own code path, kept as a permanent red-if-reverted arm.
	readonly SessionSearchService _uncached;

	public SessionSearchPoolCacheTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sesspoolcache-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_sessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), TestSchema.Sessions);
		_memoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		// The NON-DETERMINISTIC route goes ONLY to the digest discovery leg — the stage whose order the
		// cursor is bound to. Episodic hydration gets its own stable client, so its per-session reranks
		// never advance the swap counter and cannot be mistaken for the cause of anything asserted here.
		_digestLlm = new AdjacentSwapReranker();
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _memoryFactory), llm: _digestLlm);
		_episodic = new DuckDbSessionEpisodicIndex(_sessionsFactory, llm: new StableReranker());
		_termIndex = new SessionTermIndex(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions);
		_fullScanIndex = new SessionFullScanIndex(_sessions);
		_settingsResolver = new SettingsResolver(new SettingsStore(_db.Factory()), new NoSecrets());
		_pools = new PoolCacheHarness();
		_usage = new MemoryUsageRecorder(_memoryFactory);
		_search = new SessionSearchService(_memory, _episodic, _termIndex, _fullScanIndex, _settingsResolver,
			_sessions, rerank: null, poolCache: _pools.Cache);
		_uncached = new SessionSearchService(_memory, _episodic, _termIndex, _fullScanIndex, _settingsResolver,
			_sessions);
	}

	public void Dispose()
	{
		_episodic.Dispose();
		_usage.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_pools.Dispose();
		_db.Dispose();
		_sessionsFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_memoryFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// ── the control: this suite is only meaningful if the route really is unstable ────────────────

	[Fact]
	public async Task Control_TheDigestRerankRoute_IsWired_AndReordersBetweenTwoIdenticalCalls()
	{
		// Without this, every green result below is worthless: a deterministic fake would make the walk
		// pass whether or not the pool is cached, and a MISSING reranker (the existing suite's `llm: null`)
		// would make it pass for the third, worst reason — the defect is invisible with no cross-encoder.
		await SeedSixAsync();

		var first = await _uncached.SearchAsync(Proj, Query, sessions: 2);
		var second = await _uncached.SearchAsync(Proj, Query, sessions: 2);

		first.Discovery.Ranking.Should().Be(SearchRankingOutcome.Reranked,
			"a cross-encoder must actually run on the digest leg — this is the half SessionSearchCursorTests switches off");
		second.Discovery.Ranking.Should().Be(SearchRankingOutcome.Reranked);
		_digestLlm.Calls.Should().Be(2, "an UNCACHED pool reranks once per call — that is the cost the cache removes");
		second.DataVersion.Should().NotBe(first.DataVersion,
			"the route hands back the same documents in a different order, so a rebuilt pool stamps a different order hash");
	}

	// ── the acceptance criterion: the walk reaches the end ────────────────────────────────────────

	[Fact]
	public async Task PageWalk_OverACachedPool_ReachesTheEndOfTheDiscoveryPool()
	{
		// The card's first acceptance line: page + its own nextCursor, handed straight back, must return
		// page 2 rather than an error — and the walk must go all the way to a stop that is not "more".
		await SeedSixAsync();

		var (seen, stop, rankings) = await WalkAsync(pageSize: 2);

		seen.Should().BeEquivalentTo(Enumerable.Range(0, 6).Select(i => $"s-{i}"),
			"every discovered session must reach the caller on some page");
		seen.Should().OnlyHaveUniqueItems("a keyset seek into one frozen order re-serves nothing");
		stop.Should().Be("exhausted", "six sessions is a real exhaustion, not a pool boundary");
		rankings.Should().AllBeEquivalentTo(SearchRankingOutcome.Reranked,
			"every page reports the provenance of the pass that DECIDED the order — a degraded pool is never cached");
	}

	[Fact]
	public async Task PagesAfterTheFirst_RunNoSecondRerank()
	{
		// Requirement 5's sessions half, and the mechanism behind the fix in one number: the cross-encoder
		// is paid ONCE for the whole walk. If this ever climbs back to one-per-page, the order stamp starts
		// moving again and the cursor starts refusing itself again — the defect returns by this exact route.
		await SeedSixAsync();

		var (seen, _, _) = await WalkAsync(pageSize: 2);

		seen.Should().HaveCount(6);
		_digestLlm.Calls.Should().Be(1, "the discovery pool is materialized once and every later page is a slice of it");
	}

	[Fact]
	public async Task RaisingSessionsPastTheOldFloor_MidWalk_DoesNotRebuildThePool()
	{
		// Root-cause half of card cursor-refusal-blames-caller-for-data-shift. `sessions` is deliberately
		// EXCLUDED from the cursor fingerprint ("shapes a page, not the sequence"), but before
		// TermPoolDepth was fixed it fed the pool's own CACHE KEY via `termPool = max(3 × sessions, 50)`:
		// sessions ≤ 16 always landed on the floor (stable), sessions ≥ 17 moved the key — so raising
		// `sessions` from 10 to 20 mid-walk, something the tool description promises is free, silently
		// evicted and rebuilt the pool. That is asserted directly here: page 2 with a DIFFERENT `sessions`
		// than page 1 (crossing the old 16 boundary both ways) must not throw and must not pay a second
		// cross-encoder pass.
		await SeedSixAsync();

		var page1 = await SearchToolAsync(_search, sessions: 2);
		page1.NextCursor.Should().NotBeNull("a page size of 2 over 6 sessions must leave more to walk");

		var act = () => SearchToolAsync(_search, sessions: 20, cursor: page1.NextCursor);

		await act.Should().NotThrowAsync(
			"sessions is a page-shaping argument — the tool description promises it is free to vary");
		_digestLlm.Calls.Should().Be(1,
			"the pool's cache key must not depend on `sessions`, so page 2 reads the SAME pool page 1 built");
	}

	// ── the invariant: a REAL reordering is still refused ─────────────────────────────────────────

	[Fact]
	public async Task WithoutThePoolCache_TheSameWalkIsRefused_BecauseThereIsNoPoolToWalk()
	{
		// THE RED ARM. This is the pre-fix service (no pool cache at all), walked exactly like the test
		// above. It must still throw, and it must throw for the RIGHT reason — not "cursor is for a
		// different query" and not "the session left the pool". Two things ride on it: it is the proof the
		// scenario really does trigger a guard (so the green test above cannot be green by accident), and
		// it is the proof the fix removed a FALSE refusal rather than the refusal.
		//
		// The WORDS changed with work/rerank-route-nondeterministic-order and the change is the point: a
		// service that keeps no pool rebuilds the ranking on every page, and what a caller needs to be
		// told is that the pool their walk was reading is not there — not that "the ranking changed",
		// which reads as a fault in a ranking that did nothing wrong.
		await SeedSixAsync();

		var first = await SearchToolAsync(_uncached, sessions: 2);
		first.NextCursor.Should().NotBeNull("page 1 must hand back a cursor — the defect is that it cannot be used");

		var act = () => SearchToolAsync(_uncached, sessions: 2, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage("*ranked POOL this cursor was walking is gone*");
	}

	[Fact]
	public async Task WhenThePoolIsGone_AndTheOrderReallyMoved_TheCursorIsStillRefused()
	{
		// The residual semantics, ACCEPTED deliberately (owner, 2026-08-27): a pool lost to TTL, a process
		// restart or eviction is re-materialized, the route reorders it for real, and continuing would
		// splice two orderings — so the refusal comes back, and that is correct. Invalidate() is the pool
		// cache's declared test seam for exactly this state.
		await SeedSixAsync();

		var first = await SearchToolAsync(_search, sessions: 2);
		first.NextCursor.Should().NotBeNull();

		_pools.Cache.Invalidate(); // the pool this cursor was issued against no longer exists

		var act = () => SearchToolAsync(_search, sessions: 2, cursor: first.NextCursor);

		var refusal = await act.Should().ThrowAsync<ArgumentException>();
		refusal.WithMessage("*ranked POOL this cursor was walking is gone*",
			"the guard must survive the fix — we removed a FALSE refusal, not the refusal");
		refusal.Which.Message.Should().NotContain("ranked DIFFERENTLY",
			"a pool that expired is not a ranking that misbehaved, and the caller acts on the difference");
	}

	[Fact]
	public async Task ALiveWalkMatchesTheControlRowForRow_NotJustAsASet()
	{
		// THE anti-corruption assertion for this surface. The walk above proves every session is
		// delivered once; it does NOT prove they arrive in the order the pool actually holds, and a walk
		// spliced across two orderings delivers exactly the same SET. So the page walk is compared to the
		// single-call control by SEQUENCE — Equal, never BeEquivalentTo. "Nothing threw and all the rows
		// came back" is precisely what the silent corruption would look like from here.
		await SeedSixAsync();

		var control = (await SearchToolAsync(_search, sessions: 6)).Items.Select(i => i.SessionId).ToList();
		control.Should().HaveCount(6);

		var (seen, _, _) = await WalkAsync(pageSize: 2);

		seen.Should().Equal(control, "a live pool pages the ONE order it materialized, in sequence");
	}

	[Fact]
	public async Task Control_ScoreJitterAlone_DoesNotMoveThisSurfacesOrderStamp()
	{
		// WHY THIS FILE NEEDS ITS OWN FAKE, and why a single shape of noise would have left one surface
		// unguarded. The other measured shape — scores drifting in their low digits with every row
		// staying put — is what breaks memory and tasks, because their pool stores the CROSS-ENCODER
		// score. It cannot break sessions: RankDiscovery consumes the rerank's RANKS through RRF and the
		// pool stores the fused RRF score, so a score that moves without moving a rank leaves the
		// discovery stamp byte-identical. Hence AdjacentSwapReranker for this surface, JitterRerankClient
		// for the other two — and this test is the evidence that the two are not interchangeable.
		var jitter = new JitterRerankClient();
		var memory = new MemoryService(new MemoryStore(_db.Factory(), _memoryFactory), llm: jitter);
		var search = new SessionSearchService(memory, _episodic, _termIndex, _fullScanIndex, _settingsResolver,
			_sessions);
		await SeedSixAsync();

		var first = await search.SearchAsync(Proj, Query, sessions: 2);
		var second = await search.SearchAsync(Proj, Query, sessions: 2);

		first.Discovery.Ranking.Should().Be(SearchRankingOutcome.Reranked, "the cross-encoder must have run");
		jitter.RerankCalls.Should().BeGreaterThan(1, "both passes really reranked — no pool was kept here");
		second.DataVersion.Should().Be(first.DataVersion,
			"score jitter alone cannot move a stamp built from RRF ranks — the swap fake is what this surface needs");
	}

	// ── what a cached page must still be able to say ──────────────────────────────────────────────

	[Fact]
	public async Task ACachedPage_StillReportsTheFullScanOutcome_ItCannotRecompute()
	{
		// The opt-in scan (spec: session-fullscan-optin) runs INSIDE the pool, so page 2 never re-runs it —
		// yet `fullScanCapped` must still be answered. Null on the wire means "never requested", which next
		// to fullScanRequested:true would be a flat lie, so the fact rides with the pool. This also
		// round-trips SearchPool.PoolAnnotation through the real disk cache.
		await SeedSixAsync();
		await _settingsResolver.SetAsync(Scope.System, "$",
			new SessionFullScanSettings { SystemEnabled = true }, new SessionFullScanSettings(), updatedBy: null);
		await _settingsResolver.SetAsync(Scope.Project, Proj,
			new SessionFullScanSettings { ProjectEnabled = true }, new SessionFullScanSettings(), updatedBy: null);

		var page1 = await _search.SearchAsync(Proj, Query, sessions: 2, fullScan: true);
		var page2 = await _search.SearchAsync(Proj, Query, sessions: 2, fullScan: true,
			afterSessionId: page1.LastPoolKey);

		page1.FullScanRan.Should().BeTrue();
		page1.FullScanCapped.Should().NotBeNull("the scan ran, so the cap question has an answer");
		page2.FullScanRan.Should().BeTrue();
		page2.FullScanCapped.Should().Be(page1.FullScanCapped,
			"page 2 is served from the pool that scan built — it must report what that scan found");
		page2.DataVersion.Should().Be(page1.DataVersion);
	}

	// ── seeding + the walk ────────────────────────────────────────────────────────────────────────

	static SessionMessageInput[] Msgs(params string[] contents) =>
		contents.Select(c => new SessionMessageInput("user", c)).ToArray();

	// A chat-only client writes the digests — independent of the reranking client under test, so
	// distillation never advances the swap counter.
	Task<int> Distill() =>
		new SessionDigestJob(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _memory, new EchoChat(),
				logger: null, quietPeriod: NoQuiet)
			.DrainAllAsync(CancellationToken.None);

	// Six discoverable sessions — enough that a pageSize of 2 needs three pages, and enough that the
	// reranker has ranks 1 and 2 to swap.
	async Task SeedSixAsync()
	{
		for (var i = 0; i < 6; i++)
			await _sessions.UpsertAsync(Proj, $"s-{i}", "claude-code",
				Msgs($"разговор {i} про векторизацию индекса", "прочее"));
		(await Distill()).Should().Be(6);
		_digestLlm.Calls.Should().Be(0, "seeding must not spend a rerank — the counts below are about search");
	}

	// The walk as a CALLER does it: through the MCP verb, handing `nextCursor` straight back, stopping on
	// the declared stop reason rather than on a missing cursor.
	async Task<(List<string> Seen, string? Stop, List<SearchRankingOutcome?> Rankings)> WalkAsync(int pageSize)
	{
		var seen = new List<string>();
		var rankings = new List<SearchRankingOutcome?>();
		string? cursor = null;
		for (var guard = 0; guard < 20; guard++)
		{
			var page = await SearchToolAsync(_search, sessions: pageSize, cursor: cursor);
			seen.AddRange(page.Items.Select(i => i.SessionId));
			rankings.Add(page.Retrievers?.Ranking);
			if (page.Stop != "more") return (seen, page.Stop, rankings);
			cursor = page.NextCursor;
			cursor.Should().NotBeNull("stop:\"more\" promises a way to continue");
		}
		throw new InvalidOperationException("the page walk did not terminate");
	}

	Task<PetBox.Web.Mcp.Contract.SessionSearchResultView> SearchToolAsync(
		SessionSearchService search, int sessions = 0, string? cursor = null) =>
		PetBox.Web.Mcp.SessionTools.SearchAsync(ToolHttp(), ToolFlags(), _sessions, search, _usage, Proj,
			Query, sessions, 0, false, null, cursor);

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

	// ── fixtures ──────────────────────────────────────────────────────────────────────────────────

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

	// THE MEASURED LIVE BEHAVIOUR, as small as it can be made: the same documents with the same scores,
	// except that positions 1 and 2 trade places on every other call. Nothing else varies — no dropped
	// document, no score drift, no degradation — so anything this makes fail is caused by ORDER alone.
	sealed class AdjacentSwapReranker : ILlmClient
	{
		int _calls;
		public int Calls => Volatile.Read(ref _calls);

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(new EmbedResult(
				request.Inputs.Select(_ => new float[] { 1f }).ToList(),
				new ModelIdentity("swap-embed", 1), new ServedBy("fake", "swap-embed", 1, Degraded: false)));

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default)
		{
			var n = Interlocked.Increment(ref _calls);
			var order = Enumerable.Range(0, request.Documents.Count).ToList();
			// Every other call swaps two ADJACENT ranks — the exact shape observed on the live route.
			if (n % 2 == 0 && order.Count >= 3)
				(order[1], order[2]) = (order[2], order[1]);
			var hits = order
				.Select((docIndex, rank) => new RerankHit(docIndex, 1.0 - 0.01 * rank))
				.Take(request.TopN ?? order.Count)
				.ToList();
			return Task.FromResult(new RerankResult(hits,
				new ModelIdentity("swap-rerank"), new ServedBy("fake", "swap-rerank", 1, Degraded: false)));
		}

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}

	// The episodic stage's client: reranks in place, never reorders. Present so stage 2 behaves like the
	// real thing (a cross-encoder runs) without contributing any instability of its own.
	sealed class StableReranker : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(new EmbedResult(
				request.Inputs.Select(_ => new float[] { 1f }).ToList(),
				new ModelIdentity("stable-embed", 1), new ServedBy("fake", "stable-embed", 1, Degraded: false)));

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			Task.FromResult(new RerankResult(
				request.Documents.Select((_, i) => new RerankHit(i, 1.0 - 0.01 * i))
					.Take(request.TopN ?? request.Documents.Count).ToList(),
				new ModelIdentity("stable-rerank"), new ServedBy("fake", "stable-rerank", 1, Degraded: false)));

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}
}
