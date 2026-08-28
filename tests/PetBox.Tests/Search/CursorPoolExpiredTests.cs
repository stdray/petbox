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
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Tests.Memory;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Search;

// work/rerank-route-nondeterministic-order — the cursor promised something the hardware cannot give.
//
// THE MEASURED FACT this file is built on. The cross-encoder that ranks a pool does not reproduce its
// own output. On the local route (qwen3-rerank-0.6b), 8 close paraphrases of ~50-60 tokens came back in
// three different orders across ten identical calls — 9 of 10 differed from the first; on a cloud route
// (cohere/rerank-4-fast) the sequence held but the scores moved by up to 3e-3. Neither is a defect here:
// GPU reduction kernels are not batch-invariant, so the shape of the batch a request lands in decides
// the last bits of every score. Two fixes were tried on paper and killed by measurement — rounding the
// score in the fingerprint (the reranker's own gaps between ADJACENT candidates are SMALLER than its own
// jitter, so no threshold exists), and canonicalizing the order with a tolerance (same reason, plus the
// session pool is not sorted by the score it stores).
//
// SO THE CONTRACT MOVED, not the ranking. A reranked order is a property of ONE PASS, so a cursor is
// bound to the POOL that pass materialized: while the pool lives the walk is exact, and when the pool is
// gone the walk is OVER — refused in words that name the pool, instead of today's "the ranking changed
// underneath", which blamed a ranking that had done nothing wrong.
//
// WHAT THESE TESTS HAVE TO PIN, in order of how easy each is to fake green:
//   1. the reranker is actually WIRED (`Ranking == Reranked`). With `llm: null` every defect in this
//      card is invisible — the pool degrades to RRF, which is order-stable, and the whole file passes
//      against code that has none of the fix.
//   2. the fake really moves the order stamp — otherwise every green below is green for no reason.
//   3. the two SHAPES of the noise are not interchangeable. Score jitter with the order UNCHANGED is
//      what breaks memory and tasks (their pool stores the cross-encoder score); it does NOT break
//      sessions, whose pool stores an RRF score that consumes RANKS. Adjacent-rank swaps are what break
//      sessions (SessionSearchPoolCacheTests owns that half). A file that tested one shape would miss a
//      regression on the other surface entirely.
//   4. the refusal is checked by its TEXT, not its type: all three refusals are ArgumentException, and
//      "it threw" is exactly what a wrong-but-loud diagnosis also looks like.
//   5. a walk on a LIVE pool still reproduces the control sequence ROW FOR ROW — "nothing threw and all
//      the rows arrived" is what silent corruption looks like from the outside, so the walk is compared
//      against the unpaged order with Equal, never with BeEquivalentTo.
public sealed class CursorPoolExpiredTests : IDisposable
{
	const string Proj = "proj";
	// The workspace container the cascade's far leg resolves to for a project in workspace "ws".
	static readonly string WsContainer = WorkspaceMemory.ContainerKeyFor("ws");

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly JitterRerankClient _llm = new();
	readonly PoolCacheHarness _poolHarness = new();
	readonly SearchPoolCache _poolCache;
	readonly MemoryService _memory;

	public CursorPoolExpiredTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-poolexpired-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_poolCache = _poolHarness.Cache;
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _factory), llm: _llm, poolCache: _poolCache);
	}

	public void Dispose()
	{
		_poolHarness.Dispose();
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// ── 1+2: the controls, without which every green below means nothing ──────────────────────────

	[Fact]
	public async Task Control_TheRerankRouteIsWired_AndJittersItsScoresWithoutMovingASingleRow()
	{
		// THE control for the whole file, and it asserts BOTH halves of the measured shape:
		//   * the cross-encoder really ran (Reranked) — with no reranker this card's defect cannot exist;
		//   * two rebuilds return the SAME rows in the SAME sequence, and still stamp DIFFERENT order
		//     hashes, because the scores moved in their low digits. That is form (a) — the shape that
		//     breaks memory and tasks, and the reason "just round the score" was rejected: the stored
		//     score IS the cross-encoder's, and nothing separates its noise from its signal.
		await SeedNotes();

		var first = await WholePoolAsync();
		EvictPools();
		var second = await WholePoolAsync();

		first.Retrievers!.Value.Ranking.Should().Be(SearchRankingOutcome.Reranked,
			"a cross-encoder must actually run — with llm:null the entire defect class is invisible");
		Keys(second).Should().Equal(Keys(first),
			"form (a): the ROWS and their sequence are stable — only the scores move");
		second.PoolOrderHash.Should().NotBe(first.PoolOrderHash,
			"and that alone moves the order stamp, because the pool stores the cross-encoder's score");
	}

	// ── the refusal, and its words ────────────────────────────────────────────────────────────────

	[Fact]
	public async Task AColdPool_IsRefused_InWordsThatNameThePool_NotTheRanking()
	{
		// The card's whole point. Today this refusal says "the ranked order ... came out ranked
		// DIFFERENTLY", which tells the caller a story about a ranking that misbehaved. Nothing
		// misbehaved: the pool expired, and a second cross-encoder pass is simply not the first one.
		await SeedNotes();

		var page1 = await SearchAsync(limit: 2);
		page1.NextCursor.Should().NotBeNull("page 1 must hand back a cursor");

		EvictPools();

		var act = () => SearchAsync(limit: 2, cursor: page1.NextCursor);

		var refusal = await act.Should().ThrowAsync<ArgumentException>();
		refusal.WithMessage("*ranked POOL this cursor was walking is gone*")
			.WithMessage("*Drop the cursor and start the query over*");
		refusal.Which.Message.Should().NotContain("ranked DIFFERENTLY",
			"the ranking did not change — the pool this walk was reading is simply not there any more");
	}

	[Fact]
	public async Task ALiveWalkMatchesTheControlRowForRow_AndAColdOneRefusesInsteadOfSplicing()
	{
		// THE anti-corruption test. The failure this guards against is not an exception — it is the
		// absence of one: a walk that quietly continues into a REBUILT order returns a plausible page,
		// with plausible rows, in a sequence that never existed. So both halves are asserted here:
		//
		//   * on a LIVE pool the paged walk must equal the unpaged order EXACTLY — Equal, not
		//     BeEquivalentTo, because "the same rows in some order" is precisely what a spliced walk
		//     also produces;
		//   * with the pool evicted the walk must REFUSE. "It did not throw and all the rows came back"
		//     is what the corruption looks like from the caller's seat, so it is asserted against.
		await SeedNotes();

		var control = Keys(await WholePoolAsync());
		control.Should().HaveCountGreaterThan(4, "a one-page pool would make the walk vacuous");

		var walked = new List<string>();
		string? cursor = null;
		for (var guard = 0; guard < 50; guard++)
		{
			var page = await SearchAsync(limit: 2, cursor: cursor);
			walked.AddRange(page.Items.Select(i => i.Key));
			if (page.NextCursor is null) break;
			cursor = page.NextCursor;
		}

		walked.Should().Equal(control, "a live pool pages the ONE order it materialized, row for row");

		// Same walk, one page in, with the pool taken away.
		var fresh = await SearchAsync(limit: 2);
		EvictPools();
		var act = () => SearchAsync(limit: 2, cursor: fresh.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>("a rebuilt order must never be served silently");
	}

	[Fact]
	public async Task AWriteMidWalk_IsStillRefused_AndSaysSomethingDifferentAgain()
	{
		// The invariant that keeps the fix from degenerating into "always accept": a genuinely different
		// world must still stop the walk. A write moves the container's data version, which is IN the
		// fingerprint — so this is the THIRD refusal, and it must not be confusable with the other two.
		await SeedNotes();

		var page1 = await SearchAsync(limit: 2);

		await _memory.UpsertAsync(Proj, "notes",
			[new MemoryEntryInput { Key = "lex-new", Version = 0, Type = "Project", Description = "deploy note new", Body = "the deploy keyword appears here too" }], []);

		var act = () => SearchAsync(limit: 2, cursor: page1.NextCursor);

		var refusal = await act.Should().ThrowAsync<ArgumentException>();
		refusal.WithMessage("*issued for a DIFFERENT query*");
		refusal.Which.Message.Should().NotContain("ranked POOL this cursor was walking is gone",
			"a write is not an expiry — the three diagnoses must stay tellable apart by their text alone");
	}

	// ── the CASCADE: a pool is alive only if EVERY leg's is ───────────────────────────────────────

	[Fact]
	public async Task TheWorkspaceLeg_EndsTheWalkOnItsOwn_WhenItsPoolIsGone()
	{
		// memory_search is a cascade (project ⊕ workspace) and the merged order is a SPLICE of both
		// pools, so one leg losing its pool is enough to make the merged sequence a different list. The
		// fold is an OR over the legs — asserted here from the far side: with the walk scoped to the
		// WORKSPACE container alone, the refusal can only have come from that leg's flag, which is what
		// a fold that quietly read the project leg only would fail to produce.
		await SeedNotes(WsContainer);

		var page1 = await SearchAsync(limit: 2, scope: "workspace");
		page1.Items.Should().NotBeEmpty("the workspace leg must actually be searched");

		EvictPools();

		var act = () => SearchAsync(limit: 2, scope: "workspace", cursor: page1.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage("*ranked POOL this cursor was walking is gone*");
	}

	[Fact]
	public async Task TheCascadeWalksBothLegsWhileBothPoolsLive_AndStopsWhenTheyAreGone()
	{
		// The other half: the fold must not refuse a walk whose pools are all alive. Both containers hold
		// matching rows, so the merged order really is spliced from two pools — and it must page to the
		// end exactly like the unpaged cascade, then refuse once the pools are dropped.
		await SeedNotes();
		await SeedNotes(WsContainer);

		var control = (await SearchAsync(limit: 100)).Items.Select(i => i.Scope + "/" + i.Key).ToList();
		control.Should().Contain(k => k.StartsWith("workspace/", StringComparison.Ordinal),
			"the cascade must really reach the workspace container, or this proves nothing about the fold");

		var walked = new List<string>();
		string? cursor = null;
		for (var guard = 0; guard < 50; guard++)
		{
			var page = await SearchAsync(limit: 3, cursor: cursor);
			walked.AddRange(page.Items.Select(i => i.Scope + "/" + i.Key));
			if (page.NextCursor is null) break;
			cursor = page.NextCursor;
		}

		walked.Should().Equal(control, "two live pools page as one order, in sequence");

		var fresh = await SearchAsync(limit: 3);
		EvictPools();
		var act = () => SearchAsync(limit: 3, cursor: fresh.NextCursor);
		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage("*ranked POOL this cursor was walking is gone*");
	}

	// ── the other side of the trade: an RRF order is still pageable across a cold pool ────────────

	[Fact]
	public async Task WithTheRerankRouteDown_AColdPoolStillWalksToTheEnd()
	{
		// THE DELIBERATE LIMIT of the refusal above, and it is load-bearing. Only a RERANKED order is
		// unreproducible. An RRF order — the caller chose Speed, or the rerank route is down — is
		// arithmetic over the same index: a rebuild reproduces it exactly, and the order commitment
		// proves that for free. Refusing those too would end paging for every deployment with no rerank
		// route, and for the whole length of any rerank outage, buying nothing — while this codebase's
		// standing rule is that a rerank outage must never take search down.
		await SeedNotes();
		_llm.RerankDown = true;

		var control = Keys(await WholePoolAsync());
		(await WholePoolAsync()).Retrievers!.Value.Ranking.Should().Be(SearchRankingOutcome.DegradedRrf,
			"the rerank route is down, so the pool is plain RRF — and an RRF order rebuilds identically");

		var walked = new List<string>();
		string? cursor = null;
		for (var guard = 0; guard < 50; guard++)
		{
			EvictPools(); // every page is a cold rebuild, for the whole walk
			var page = await SearchAsync(limit: 2, cursor: cursor);
			walked.AddRange(page.Items.Select(i => i.Key));
			if (page.NextCursor is null) break;
			cursor = page.NextCursor;
		}

		walked.Should().Equal(control, "a reproducible order must stay pageable when its pool is gone");
	}

	// ── seeding + plumbing ────────────────────────────────────────────────────────────────────────

	// Six rows the lexical leg can see. Enough that a page size of 2 needs three pages, and enough
	// that the reranker has neighbours whose scores can drift past each other.
	async Task SeedNotes(string? container = null)
	{
		var c = container ?? Proj;
		// The container's Projects row is LAZY in production (created on first resolve), so a test that
		// seeds it directly has to materialize it the same way the resolver would.
		if (container is not null) await WorkspaceMemory.EnsureContainerAsync(_db, "ws");
		await _memory.CreateStoreAsync(c, "notes", null);
		var rows = new List<MemoryEntryInput>();
		for (var i = 0; i < 6; i++)
			rows.Add(new MemoryEntryInput
			{
				Key = $"{(container is null ? "lex" : "ws")}-{i}",
				Version = 0,
				Type = "Project",
				Description = $"deploy note {i}",
				Body = $"the deploy keyword appears here {i}",
			});
		await _memory.UpsertAsync(c, "notes", rows, []);
	}

	// Drop every stored pool — the state a page reaches after the TTL, a restart or an eviction, which
	// a test cannot wait for. Invalidate() is the pool cache's declared seam for exactly this.
	void EvictPools() => _poolCache.Invalidate();

	// The SERVICE's own view of one whole ranked pool: the control order, and the order stamp the MCP
	// view does not carry.
	Task<MemoryEntrySearchResult> WholePoolAsync() =>
		_memory.SearchEntriesAsync(Proj, new SearchRequest<MemoryEntryFilter, MemorySortBy>
		{
			Query = "deploy",
			Filter = new MemoryEntryFilter(null, null),
			Limit = 20,
			BodyLen = 0,
			WholePool = true,
			RankingMode = SearchRankingMode.Precision,
		}, CancellationToken.None);

	static List<string> Keys(MemoryEntrySearchResult r) => r.Hits.Select(h => h.Entry.Key).ToList();

	Task<MemorySearchResultView> SearchAsync(int? limit = null, string? cursor = null, string? scope = null) =>
		MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(),
			"deploy", scope, null, null, null, null, limit, 0, false, null, cursor);

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
}
