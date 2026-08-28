using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Tests.Memory;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Search;

// The review findings on paging (work/search-results-pageable). Every test here was RED before its fix.
//
// The thread running through all of them, in the reviewer's words: the implementation kept believing
// that "the data did not change" means "the world did not change". A retriever going down, a caller
// changing page size, a response budget cutting a page short — none of those touch data, all of them
// were enough to lose rows silently, which is the one outcome this feature was built to prevent.
public sealed class PagePoolRegressionTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly FlakyLlmClient _llm = new();
	readonly PoolCacheHarness _poolHarness = new();
	readonly SearchPoolCache _poolCache;
	readonly MemoryService _memory;

	public PagePoolRegressionTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-poolreg-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_store = new MemoryStore(_db.Factory(), _factory);
		_poolCache = _poolHarness.Cache;
		_memory = new MemoryService(_store, llm: _llm, poolCache: _poolCache);
	}

	public void Dispose()
	{
		_poolHarness.Dispose();
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "memory:read,memory:write")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	Task Remember(string text) =>
		MemoryTools.RememberAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, text, scope: "project");

	Task<MemorySearchResultView> Search(string? q = null, int? limit = null, string? cursor = null) =>
		MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(),
			q, "project", null, null, null, null, limit, null, false, null, cursor);

	// ── B1: a retriever outage between pages must not delete rows from the walk ──────────────

	// Vectors are materialized off the write path, so a test that needs the semantic leg drains first
	// (with the SAME embedder the query path uses, so the model/dim guard matches).
	async Task DrainVectors(string store)
	{
		LinqToDB.Data.DataConnection Connect() => _factory.NewEnsuredConnection(Proj);
		var worker = new PetBox.Core.Search.AsyncVectorizationWorker(
			MemoryCursors.Vector(store),
			new MemorySearchSource(Connect, Proj, store),
			new VectorSearchIndex(Connect, new LlmClientEmbedder(_llm, Proj)),
			new SqliteIndexCursorStore(Connect));
		await worker.DrainAsync();
	}

	// A pool with rows only the VECTOR leg can surface: `sem-*` entries carry the near-query marker but
	// NOT the query token, so the lexical leg cannot see them at all. They are what an Embed outage
	// makes disappear — and what page 2 must still deliver.
	async Task SeedLexicalAndSemantic()
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		var rows = new List<PetBox.Memory.Contract.MemoryEntryInput>();
		for (var i = 0; i < 3; i++)
			rows.Add(new() { Key = $"lex-{i}", Version = 0, Type = "Project", Description = $"deploy note {i}", Body = "the deploy keyword appears here" });
		for (var i = 0; i < 3; i++)
			rows.Add(new() { Key = $"sem-{i}", Version = 0, Type = "Project", Description = $"note {i}", Body = FakeLlmClient.NearQueryMarker + $" unrelated words {i}" });
		await _memory.UpsertAsync(Proj, "notes", rows, []);
		await DrainVectors("notes");
	}

	[Fact]
	public async Task B1_EmbedOutageBetweenPages_DoesNotLoseTheVectorOnlyRowsFromTheCachedPool()
	{
		// THE regression, and the reason it needs vector-ONLY rows to show itself. Page 1 is healthy and
		// caches a ranked pool holding both lexical and semantic candidates. Then Embed goes down —
		// AVAILABILITY, not data — so the change stamp is identical, the cache key matches and the cursor
		// is (correctly) still valid. The old code re-ran the search on that cache hit and kept only what
		// the re-derived union returned; with the vector leg throwing, every `sem-*` row silently
		// evaporated from a walk that had already promised them. Hydrating the stored ADDRESSES cannot
		// express that bug — the pool IS the answer.
		await SeedLexicalAndSemantic();

		var whole = (await Search(q: "deploy", limit: 100)).Items.Select(i => i.Key).ToList();
		whole.Should().Contain(k => k.StartsWith("sem-"), "the pool must hold vector-only rows for this test to mean anything");

		var first = await Search(q: "deploy", limit: 2);
		first.NextCursor.Should().NotBeNull();
		var seen = first.Items.Select(i => i.Key).ToList();

		_llm.EmbedDown = true; // nothing about the DATA changes here

		string? cursor = first.NextCursor;
		for (var guard = 0; guard < 50 && cursor is not null; guard++)
		{
			var page = await Search(q: "deploy", limit: 2, cursor: cursor);
			seen.AddRange(page.Items.Select(i => i.Key));
			cursor = page.NextCursor;
		}

		seen.Should().Equal(whole, "an Embed outage is not a fact about any row — the walk must still cover the pool");
		seen.Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public async Task B1_EmbedOutageBetweenPages_DoesNotReportExhaustedWhileRowsRemain()
	{
		// The consumer-facing half of the same defect: when the lost rows were the whole remainder, the
		// page came back empty and said "exhausted" — "we stopped looking" wearing "there is no more".
		await SeedLexicalAndSemantic();

		var whole = (await Search(q: "deploy", limit: 100)).Items.Select(i => i.Key).ToList();
		var first = await Search(q: "deploy", limit: 2);
		first.Stop.Should().Be("more");

		_llm.EmbedDown = true;

		var delivered = first.Items.Select(i => i.Key).ToList();
		var page = await Search(q: "deploy", limit: 2, cursor: first.NextCursor);
		delivered.AddRange(page.Items.Select(i => i.Key));

		// Whatever it says, it must not claim the selection ran out while the pool still owes rows.
		if (page.Stop == "exhausted")
			delivered.Should().BeEquivalentTo(whole,
				"'exhausted' means every matching row was ranked AND served — never 'a retriever blinked'");
	}

	// ── B2: page size must not change the pool ───────────────────────────────────────────────

	[Fact]
	public async Task B2_ChangingLimitBetweenPages_KeepsTheSamePool_AndTheWalkStaysWhole()
	{
		// The tool contract promises `limit` may vary between pages. It used to also size the candidate
		// depth (max(3×limit, 50)), so page 1 with no limit (depth 60) and page 2 with limit:10 (depth 50)
		// ranked DIFFERENT pools — while the fingerprint, which deliberately excludes `limit`, happily
		// accepted the cursor. Skips and duplicates, no error anywhere.
		// The depth bounds the VECTOR leg (the lexical one is enumerable and returns everything), so the
		// bug only shows with enough vector-ONLY rows that depth 60 and depth 50 admit different sets of
		// them. 70 marker rows do that: page 1 at depth 60 ranks sixty of them, page 2 at depth 50 ranks
		// fifty, and the ten in between fall out of a walk that had already promised them.
		await _memory.CreateStoreAsync(Proj, "notes", null);
		var rows = new List<PetBox.Memory.Contract.MemoryEntryInput>
		{
			new() { Key = "lex", Version = 0, Type = "Project", Description = "deploy note", Body = "the deploy keyword appears here" },
		};
		for (var i = 0; i < 70; i++)
			rows.Add(new() { Key = $"sem-{i:d2}", Version = 0, Type = "Project", Description = $"note {i}", Body = FakeLlmClient.NearQueryMarker + $" unrelated {i}" });
		await _memory.UpsertAsync(Proj, "notes", rows, []);
		await DrainVectors("notes");

		var whole = (await Search(q: "deploy", limit: 200)).Items.Select(i => i.Key).ToList();
		whole.Count.Should().BeGreaterThan(50, "the pool must be deeper than the smaller candidate depth");

		var first = await Search(q: "deploy");            // no limit → default cap
		var seen = first.Items.Select(i => i.Key).ToList();
		string? cursor = first.NextCursor;
		for (var guard = 0; guard < 50 && cursor is not null; guard++)
		{
			var page = await Search(q: "deploy", limit: 3, cursor: cursor); // a DIFFERENT page size
			seen.AddRange(page.Items.Select(i => i.Key));
			cursor = page.NextCursor;
		}

		seen.Should().OnlyHaveUniqueItems("a page-size change must not re-serve a row");
		seen.Should().BeEquivalentTo(whole, "nor lose one");
	}

	[Fact]
	public void B2_PagedCandidateDepth_IsFixed_NotDerivedFromTheCallersLimit()
	{
		// The property itself, stated once: in paged mode the depth is a constant, so the pool is a
		// property of the QUERY rather than of how the caller happened to ask for it.
		MemoryService.PagedCandidateDepth.Should().Be(60);
		PetBox.Tasks.Services.TasksService.PagedCandidateDepth.Should().Be(50);
	}

	// ── A2: a degraded pool must not be cached ───────────────────────────────────────────────

	[Fact]
	public async Task A2_DegradedPool_IsNotCached_SoRecoveryIsImmediate()
	{
		// Caching a degraded pool pins a half-answer AND its stale provenance for the whole TTL, so every
		// repeat of the query keeps hitting an outage that already healed — turning a self-healing blip
		// into ten minutes of quietly worse results.
		for (var i = 0; i < 4; i++)
			await Remember($"deploy release note {i}");

		_llm.EmbedDown = true;
		var degraded = await Search(q: "deploy", limit: 2);

		degraded.Retrievers!.Degraded.Should().BeTrue("the vector leg threw — this pool is a degradation");
		_poolCache.Stores.Should().Be(0, "a degraded pool is cheap to recompute and expensive to keep");

		_llm.EmbedDown = false;
		var healthy = await Search(q: "deploy", limit: 2);

		healthy.Retrievers!.Degraded.Should().BeFalse("recovery must be visible on the very next call");
		healthy.Retrievers!.Ranking.Should().Be(SearchRankingOutcome.Reranked);
		_poolCache.Stores.Should().Be(1, "a healthy, actually-reranked pool is the one worth keeping");
	}

	// ── A3: the memory change stamp must move on a DELETE ────────────────────────────────────

	[Fact]
	public async Task A3_ChangeStamp_Moves_WhenAnEntryIsDeleted()
	{
		// The earlier stamp was MAX(Version) + row COUNT, on the belief that a delete appends a revision.
		// It does not: TemporalStore stamps ActiveTo on the EXISTING row and inserts nothing, so neither
		// aggregate moved and a pure delete was invisible — a cursor survived the disappearance of the
		// very rows it was walking.
		await MemoryTools.RememberAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			"deploy release note alpha", scope: "project");
		var victim = await MemoryTools.RememberAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			"deploy release note beta", scope: "project");

		var stores = (await _memory.ListStoresAsync(Proj)).Select(s => s.Name).ToList();
		var before = await _memory.ChangeStampAsync(Proj, stores);

		var victimRow = (await _memory.SearchEntriesAsync(Proj,
			new SearchRequest<PetBox.Memory.Contract.MemoryEntryFilter, PetBox.Memory.Contract.MemorySortBy>())).Hits
			.Single(h => h.Entry.Key == victim.Key);
		await _memory.UpsertAsync(Proj, victim.Store, [],
			[new PetBox.Memory.Contract.MemoryDelete(victim.Key, victimRow.Entry.Version)], atomic: true);

		var after = await _memory.ChangeStampAsync(Proj, stores);
		after.Should().NotBe(before, "a delete is a change of the ordering basis and must invalidate a cursor");
	}

	[Fact]
	public async Task A3_Cursor_IsRefused_AfterAnEntryIsDeletedMidWalk()
	{
		// The stamp exists to make this happen. Without the delete showing up in it, page 2 would have
		// been served happily over a pool that no longer matched reality.
		for (var i = 0; i < 6; i++)
			await Remember($"deploy release note {i}");
		var first = await Search(q: "deploy", limit: 2);
		first.NextCursor.Should().NotBeNull();

		var store = first.Items[0].Store;
		var doomed = first.Items[0].Key;
		await _memory.UpsertAsync(Proj, store, [],
			[new PetBox.Memory.Contract.MemoryDelete(doomed, first.Items[0].Version)], atomic: true);

		var act = () => Search(q: "deploy", limit: 2, cursor: first.NextCursor);

		// card cursor-refusal-blames-caller-for-data-shift: the delete is a DATA change, not a caller
		// argument change — the refusal must say so instead of "DIFFERENT query".
		var refusal = await act.Should().ThrowAsync<ArgumentException>();
		refusal.WithMessage("*DATA this cursor was reading has changed*");
		refusal.Which.Message.Should().NotContain("DIFFERENT query");
	}

	// ── A1: a degradation in ONE cascade leg must not be merged into success ─────────────────

	[Fact]
	public void A1_MergeRanking_LetsDegradationDominate_NotSuccess()
	{
		// The merged answer is ONE list: if any part of it was never reranked, the list was not. The old
		// rule resolved toward the flattering value, so the PERMANENT arrangement "project has a rerank
		// route, workspace does not" reported Reranked forever while half the rows were plain RRF —
		// discarding the exact distinction the tri-state was introduced to carry.
		MemoryTools.MergeRanking(SearchRankingOutcome.Reranked, SearchRankingOutcome.DegradedRrf)
			.Should().Be(SearchRankingOutcome.DegradedRrf);
		MemoryTools.MergeRanking(SearchRankingOutcome.DegradedRrf, SearchRankingOutcome.Reranked)
			.Should().Be(SearchRankingOutcome.DegradedRrf);

		// Unchanged where there is nothing to hide.
		MemoryTools.MergeRanking(SearchRankingOutcome.Reranked, SearchRankingOutcome.Reranked)
			.Should().Be(SearchRankingOutcome.Reranked);
		MemoryTools.MergeRanking(SearchRankingOutcome.ChosenRrf, SearchRankingOutcome.ChosenRrf)
			.Should().Be(SearchRankingOutcome.ChosenRrf);
		MemoryTools.MergeRanking(null, SearchRankingOutcome.Reranked)
			.Should().Be(SearchRankingOutcome.Reranked);
	}

	// ── C: the ORDER COMMITMENT — a rebuilt pool that ranks differently is refused ───────────
	//
	// The residual path after B1/A2 were fixed. Hydrating addresses protects a walk whose pool is still
	// CACHED; it says nothing about a pool that was EVICTED (TTL, capacity, process restart) and rebuilt.
	// A rebuild during a rerank outage — or after one healed — returns the same rows in a different
	// sequence, with nothing written, so every data stamp agrees and the fingerprint matches. The token
	// now commits to the ORDER as well, so that becomes a loud refusal.

	[Fact]
	public async Task C_PoolRebuiltWhileRerankIsDown_IsRefused_NotSpliced()
	{
		// Page 1 healthy and reranked. Evict the pool, then take the rerank route down: page 2 rebuilds
		// as plain RRF — same rows, different order — and an identity seek into it would skip and repeat
		// rows in silence.
		await SeedLexicalAndSemantic();

		var first = await Search(q: "deploy", limit: 2);
		first.Retrievers!.Ranking.Should().Be(SearchRankingOutcome.Reranked);
		first.NextCursor.Should().NotBeNull();

		EvictPools();
		_llm.EmbedDown = true; // nothing written; only the route went away

		var act = () => Search(q: "deploy", limit: 2, cursor: first.NextCursor);

		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*ranked DIFFERENTLY*")
			// the message must not blame the caller's arguments, nor claim the row vanished
			.WithMessage("*Your arguments are fine*");
	}

	[Fact]
	public async Task C_PoolRebuiltAfterRerankRecovered_IsRefused_NotSpliced()
	{
		// The commoner shape, and the one memory could not see at all. Page 1 lands during an outage, so
		// its pool is degraded and therefore NOT cached (A2) — meaning page 2 is always a rebuild. By then
		// the route has recovered and the rebuild is reranked: same rows, same stamp, same fingerprint,
		// different order. Minutes-long recoveries are the observed pattern, so this is the everyday case.
		await SeedLexicalAndSemantic();

		_llm.EmbedDown = true;
		var first = await Search(q: "deploy", limit: 2);
		first.Retrievers!.Degraded.Should().BeTrue();
		first.NextCursor.Should().NotBeNull();
		_poolCache.Stores.Should().Be(0, "a degraded pool is never cached — so page 2 must rebuild");

		_llm.EmbedDown = false; // the route heals between pages

		var act = () => Search(q: "deploy", limit: 2, cursor: first.NextCursor);

		// The WORDS moved with work/rerank-route-nondeterministic-order, and the refusal did not. Page 2
		// here IS a fresh cross-encoder pass over a pool nobody kept, which is precisely the state that
		// can no longer be walked into: what the caller must be told is that the pool their walk was
		// reading is not there, not that "the ranking changed", which sends them hunting a ranking bug.
		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage("*ranked POOL this cursor was walking is gone*");
	}

	[Fact]
	public async Task C_AnRrfPoolRebuiltIdentically_IsAccepted_SoTheGuardIsNotAWall()
	{
		// The other half of the contract: the check must fire on DRIFT, not on rebuilding — otherwise
		// "cold page" would mean "broken page" and the guard would be useless.
		//
		// WHAT CHANGED HERE, and it is the deliberate LIMIT of the pool refusal above. This test used to
		// walk a RERANKED pool across evictions and expect it through, on the premise that "an evicted
		// pool rebuilt over unchanged data through the same route reproduces the same order". Measurement
		// killed that premise (work/rerank-route-nondeterministic-order): a cross-encoder does NOT
		// reproduce its own order — 9 of 10 identical calls came back permuted on the live route — so a
		// reranked walk now ends with its pool, and CursorPoolExpiredTests asserts that it does.
		//
		// The premise is still exactly true for an RRF order, which is what this test now walks: with the
		// rerank route down, the pool is plain arithmetic over the same index, a rebuild reproduces it
		// byte for byte, and the ORDER COMMITMENT proves that for free. This case must keep working, and
		// not as a nicety: refusing it would end paging for every deployment with no rerank route and for
		// the whole length of any rerank outage — while the standing rule here is that a rerank outage
		// must never take search down.
		await SeedLexicalAndSemantic();
		_llm.EmbedDown = true; // no cross-encoder for the whole walk: an honest RRF degradation

		var whole = (await Search(q: "deploy", limit: 100)).Items.Select(i => i.Key).ToList();
		var first = await Search(q: "deploy", limit: 2);
		first.Retrievers!.Ranking.Should().Be(SearchRankingOutcome.DegradedRrf,
			"the premise of this test is a REPRODUCIBLE order — assert it really is one");
		var seen = first.Items.Select(i => i.Key).ToList();

		EvictPools(); // every later page is now a cold rebuild

		string? cursor = first.NextCursor;
		for (var guard = 0; guard < 50 && cursor is not null; guard++)
		{
			EvictPools(); // stay cold for the whole walk
			var page = await Search(q: "deploy", limit: 2, cursor: cursor);
			seen.AddRange(page.Items.Select(i => i.Key));
			cursor = page.NextCursor;
		}

		seen.Should().Equal(whole, "a faithful rebuild must let the walk continue");
	}

	[Fact]
	public void C_OrderHash_ChangesWithSequence_AndWithScore()
	{
		// Both halves matter. A reordering is the obvious one; a re-SCORING matters because RRF scores
		// (~1/60 scale) and cross-encoder scores are different numbers even where the sequence survives,
		// and that is exactly the drift the ranking-mode flip produces.
		var a = KeysetCursor.OrderHashOf([("x", 1.0), ("y", 0.5)]);
		var reordered = KeysetCursor.OrderHashOf([("y", 0.5), ("x", 1.0)]);
		var rescored = KeysetCursor.OrderHashOf([("x", 0.9), ("y", 0.5)]);
		var same = KeysetCursor.OrderHashOf([("x", 1.0), ("y", 0.5)]);

		reordered.Should().NotBe(a);
		rescored.Should().NotBe(a);
		same.Should().Be(a, "an identical order must hash identically or every cold page would fail");
	}

	[Fact]
	public void C_AVersionOneToken_IsRefused_BecauseItCarriesNoOrderCommitment()
	{
		// Tokens minted before the order commitment existed promise nothing about ranking, so honouring
		// one would silently reopen the hole for the length of one deploy's in-flight walks.
		var v1 = Convert.ToBase64String(
			System.Text.Encoding.UTF8.GetBytes("""{"v":1,"f":"abc","s":"","k":"k","b":"b"}"""));

		var act = () => KeysetCursor.Decode(v1, "abc", "memory_search");

		act.Should().Throw<ArgumentException>().WithMessage("*older token format*");
	}

	[Fact]
	public void C2_AVersionTwoToken_IsRefused_BecauseItCarriesNoDataStampCommitment()
	{
		// Same precedent, one version later (card cursor-refusal-blames-caller-for-data-shift). A v2
		// token has its data version baked INSIDE `f` (the fingerprint) instead of carrying it separately
		// in `d` — there is nothing to compare a v3 caller's args-only fingerprint against, so honouring
		// it would either (a) never match (v2's `f` always differs from a v3-computed one, since v2's
		// bakes in a stamp v3's does not) or, worse, (b) match by coincidence and skip the data check
		// entirely. Refused outright, like v1 before it, rather than guessing.
		var v2 = Convert.ToBase64String(
			System.Text.Encoding.UTF8.GetBytes("""{"v":2,"f":"abc","s":"","k":"k","b":"b","o":"ord-1"}"""));

		var act = () => KeysetCursor.Decode(v2, "abc", "memory_search");

		act.Should().Throw<ArgumentException>().WithMessage("*older token format*");
	}

	// Drop every cached pool — the state in which a page must REBUILD rather than reuse.
	//
	// It used to reach that state by overflowing a 64-entry capacity. There is no capacity any more,
	// and a disk cache does not lose pools to a restart either, so the only thing that still drops one
	// is TTL — which a test cannot wait for. Invalidate() is the seam that stands in for it.
	void EvictPools() => _poolCache.Invalidate();

	// ── A4: identity resume is only sound while the row has not MOVED ────────────────────────

	[Fact]
	public void A4_IdentityResume_IsRefusedWhenTheBoundaryRowMovedAlongTheSortAxis()
	{
		// A volatile axis (sessions list by Updated, which every write moves) turns "resume after that
		// row, wherever it now sits" into "skip everything it jumped over". The documented anomaly
		// promises ONE row may be missed; losing the middle of the walk is a different thing.
		//
		// Rows ordered by their sort value. The token names row "b" AT sort value "20" — but b has since
		// been touched and now sorts last at "99". Resuming after b's new position would swallow c and d.
		var rows = new[] { ("10", "a"), ("30", "c"), ("40", "d"), ("99", "b") };
		var cursor = new KeysetCursor("fp", "20", "b", "board");

		var rest = KeysetCursor.Advance(rows, cursor,
			r => (r.Item1, r.Item2, "board"),
			static (x, y) => int.Parse(x).CompareTo(int.Parse(y)), desc: false, "test");

		rest.Select(r => r.Item2).Should().Equal(["c", "d", "b"],
			"the walk resumes at the POSITION the token described, not at wherever the row wandered to");
	}

	[Fact]
	public void A4_IdentityResume_StillUsedWhenTheRowHasNotMoved()
	{
		// The fast path must survive: an unmoved boundary row still resumes by identity, which is exact.
		var rows = new[] { ("10", "a"), ("20", "b"), ("30", "c") };
		var cursor = new KeysetCursor("fp", "20", "b", "board");

		var rest = KeysetCursor.Advance(rows, cursor,
			r => (r.Item1, r.Item2, "board"),
			static (x, y) => int.Parse(x).CompareTo(int.Parse(y)), desc: false, "test");

		rest.Select(r => r.Item2).Should().Equal(["c"]);
	}
}
