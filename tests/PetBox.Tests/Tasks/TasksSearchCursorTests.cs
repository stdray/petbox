using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Search;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// tasks_search LISTING pagination (work/tasks-search-listing-keyset-cursor): the response
// budget is a constant and a board is not, so past a certain size the tail of a listing was
// simply unreachable — there was nothing to ask for it with. `cursor`/`nextCursor` is that
// missing half: an opaque KEYSET token (PetBox.Core.Contract.KeysetCursor) naming the last row
// actually emitted, seeked BEFORE the budget cut so a skipped prefix never spends budget twice.
//
// The load-bearing test here is the CONCATENATION INVARIANT: walking a listing page by page
// must yield exactly the rows, in exactly the order, that the same unpaged listing yields on an
// unchanged board. If that holds, pagination provably changed neither selection nor order — no
// eval infrastructure needed, because no ranking code is involved (a listing is a deterministic
// DB sort). The rest pins the deliberate REFUSALS: q-mode and sort:relevance get no cursor at
// all (their order is re-derived per call over a bounded candidate pool, so a resume token
// would promise a tail that does not exist), and a token from a different query is an error
// rather than a silent restart inside another ordering.
// Shared per-class host (work share-fixtures-across-per-test-classes, wave 2): the migrated core +
// tasks DB files are the expensive part of the constructor — the fixture owns the files, the test
// class rebuilds the (cheap) service graph, INCLUDING a fresh SearchPoolCache, per test. Per-test
// DATA isolation is TestDataReset.WipeAllTables over the tasks file plus a TaskBoards wipe in core
// (the board catalog lives there — TaskBoardStore) — not TestDirs.ResetDbFile, which costs more than
// a fresh templated copy (see TestDataReset).
public sealed class TasksSearchCursorFixture : IDisposable
{
	public const string Proj = "proj";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<TasksDb> Factory { get; }

	public TasksSearchCursorFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-searchcursor-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		Db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		Db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		Factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
	}

	public void Reset()
	{
		Db.TaskBoards.Where(b => b.ProjectKey == Proj).Delete();
		using var tasks = Factory.NewEnsuredConnection(Proj);
		TestDataReset.WipeAllTables(tasks);
	}

	public void Dispose()
	{
		Db.Dispose();
		Factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}
}

public sealed class TasksSearchCursorTests : IClassFixture<TasksSearchCursorFixture>
{
	const string Proj = TasksSearchCursorFixture.Proj;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;
	// Injected explicitly (production wires it as a singleton) so the q-mode tests can OBSERVE whether a
	// page built a new pool or reused the stored one — the only end-to-end signal that requirement 5
	// ("реранк считается один раз на пул") is actually holding rather than merely intended. Fresh per
	// test (an instance field, not fixture state) — a pool cached from a PREVIOUS test's rows must
	// never be served to this one.
	readonly SearchPoolCache _poolCache = new();

	public TasksSearchCursorTests(TasksSearchCursorFixture fx)
	{
		fx.Reset();
		_db = fx.Db;
		_factory = fx.Factory;
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory),
			poolCache: _poolCache);
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.test");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	async Task Seed(string board, string nodesJson)
	{
		if (!await _tasks.BoardExistsAsync(Proj, board))
			await _tasks.CreateBoardAsync(Proj, board, null, null, null);
		await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, board, McpInputs.NodesJson(nodesJson));
	}

	// `count` nodes with `bodyChars`-char bodies — big bodies are how the response budget, not
	// `limit`, becomes the thing that cuts the page.
	async Task SeedMany(string board, int count, int bodyChars)
	{
		var body = new string('b', bodyChars);
		var rows = string.Join(",", Enumerable.Range(0, count).Select(i =>
			$$"""{"key":"node-{{i:d3}}","status":"Todo","title":"Node {{i}}","body":"{{body}}"}"""));
		await Seed(board, $"[{rows}]");
	}

	Task<TaskSearchResultView> Search(
		string? q = null, string? board = null, string[]? status = null, SortInput? sort = null,
		string? groupBy = null, int? bodyLen = null, int? limit = null, string? cursor = null) =>
		TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, q, board, null, status, null,
			false, sort, groupBy, bodyLen, limit, false, null, null, cursor);

	// Walk a listing to exhaustion, returning every key in page order. `page` runs one page for a
	// given cursor; the walk stops when the response stops issuing one.
	static async Task<List<string>> WalkAsync(Func<string?, Task<TaskSearchResultView>> page)
	{
		var keys = new List<string>();
		string? cursor = null;
		for (var guard = 0; guard < 200; guard++)
		{
			var res = await page(cursor);
			keys.AddRange(res.Nodes.Select(n => n.Key));
			if (res.NextCursor is null) return keys;
			cursor = res.NextCursor;
		}
		throw new InvalidOperationException("page walk did not terminate — nextCursor never went away");
	}

	// ── THE invariant: pages == the whole thing ──────────────────────────────────────────────

	[Fact]
	public async Task PageWalk_LimitSized_ConcatenatesToTheUnpagedListing()
	{
		await Seed("b", """
			[{"key":"a","status":"Todo","title":"A","body":"x","priority":30},
			 {"key":"b","status":"Todo","title":"B","body":"x","priority":10},
			 {"key":"c","status":"Todo","title":"C","body":"x","priority":20},
			 {"key":"d","status":"Todo","title":"D","body":"x","priority":10},
			 {"key":"e","status":"Todo","title":"E","body":"x","priority":40},
			 {"key":"f","status":"Todo","title":"F","body":"x","priority":25},
			 {"key":"g","status":"Todo","title":"G","body":"x","priority":5}]
			""");

		var whole = (await Search(board: "b")).Nodes.Select(n => n.Key).ToList();
		whole.Count.Should().Be(7); // the reference read fits in one response

		var paged = await WalkAsync(c => Search(board: "b", limit: 2, cursor: c));

		paged.Should().Equal(whole); // same rows, same order — pagination is presentation only
	}

	[Fact]
	public async Task PageWalk_UnderDescendingSort_ConcatenatesToTheUnpagedListing()
	{
		// desc inverts only the PRIMARY key; the (key, board) tie-breakers stay ascending, and the
		// keyset predicate has to model that exactly or the two priority:10 rows split wrong.
		await Seed("b", """
			[{"key":"a","status":"Todo","title":"A","body":"x","priority":30},
			 {"key":"b","status":"Todo","title":"B","body":"x","priority":10},
			 {"key":"c","status":"Todo","title":"C","body":"x","priority":20},
			 {"key":"d","status":"Todo","title":"D","body":"x","priority":10},
			 {"key":"e","status":"Todo","title":"E","body":"x","priority":40}]
			""");
		var sort = new SortInput { By = "priority", Desc = true };

		var whole = (await Search(board: "b", sort: sort)).Nodes.Select(n => n.Key).ToList();
		var paged = await WalkAsync(c => Search(board: "b", sort: sort, limit: 1, cursor: c));

		paged.Should().Equal(whole);
	}

	[Fact]
	public async Task PageWalk_UnderTitleSort_ConcatenatesToTheUnpagedListing()
	{
		await Seed("b", """
			[{"key":"k1","status":"Todo","title":"zebra","body":"x"},
			 {"key":"k2","status":"Todo","title":"Alpha","body":"x"},
			 {"key":"k3","status":"Todo","title":"middle","body":"x"},
			 {"key":"k4","status":"Todo","title":"Beta","body":"x"}]
			""");
		var sort = new SortInput { By = "title" };

		var whole = (await Search(board: "b", sort: sort)).Nodes.Select(n => n.Key).ToList();
		var paged = await WalkAsync(c => Search(board: "b", sort: sort, limit: 1, cursor: c));

		paged.Should().Equal(whole);
	}

	[Fact]
	public async Task PageWalk_WhenTheBudgetIsWhatCuts_ReachesTheTail()
	{
		// The card's actual complaint: no `limit` involved at all — 60 nodes of full bodies simply
		// do not fit in one ~30k response, and before the cursor the omitted rows were unreachable.
		const int total = 60;
		await SeedMany("big", total, 1000);

		var firstPage = await Search(board: "big", bodyLen: -1);
		firstPage.Truncated.Should().BeTrue();
		firstPage.Omitted.Should().BeGreaterThan(0);
		firstPage.NextCursor.Should().NotBeNull("a cut listing must hand back a way to continue");
		firstPage.Hint.Should().Contain("nextCursor");

		var paged = await WalkAsync(c => Search(board: "big", bodyLen: -1, cursor: c));

		paged.Should().Equal(Enumerable.Range(0, total).Select(i => $"node-{i:d3}"));
		paged.Should().OnlyHaveUniqueItems(); // a keyset seek re-serves nothing
	}

	[Fact]
	public async Task CompletePage_IssuesNoCursor()
	{
		await Seed("b", """[{"key":"only","status":"Todo","title":"O","body":"x"}]""");

		var res = await Search(board: "b");

		res.NextCursor.Should().BeNull("absence of nextCursor IS the end-of-list signal");
		res.Truncated.Should().BeNull();
	}

	[Fact]
	public async Task LastPage_OfAWalk_IssuesNoCursor()
	{
		await Seed("b", """
			[{"key":"a","status":"Todo","title":"A","body":"x","priority":1},
			 {"key":"b","status":"Todo","title":"B","body":"x","priority":2}]
			""");

		var first = await Search(board: "b", limit: 1);
		first.NextCursor.Should().NotBeNull();

		var second = await Search(board: "b", limit: 1, cursor: first.NextCursor);
		second.Nodes.Select(n => n.Key).Should().Equal("b");
		second.NextCursor.Should().BeNull();
	}

	[Fact]
	public async Task DeletedBoundaryRow_DoesNotRestartTheWalk()
	{
		// The identity of the row a token names can vanish between pages; the keyset predicate is
		// the fallback, so the walk must resume at the same PLACE, not at the top.
		await Seed("b", """
			[{"key":"a","status":"Todo","title":"A","body":"x","priority":1},
			 {"key":"b","status":"Todo","title":"B","body":"x","priority":2},
			 {"key":"c","status":"Todo","title":"C","body":"x","priority":3}]
			""");

		var first = await Search(board: "b", limit: 2);
		first.Nodes.Select(n => n.Key).Should().Equal("a", "b");
		await Seed("b", """[{"key":"b","deleted":true,"version":1}]"""); // the boundary row itself

		var second = await Search(board: "b", limit: 2, cursor: first.NextCursor);

		second.Nodes.Select(n => n.Key).Should().Equal("c");
	}

	// ── q PAGES TOO (work/search-results-pageable) ───────────────────────────────────────────
	//
	// The card's complaint verbatim: «Поискав „e2e", я не могу итерировать страницами по 10». `q` used
	// to be a REFUSAL to navigate, justified by a claim about the implementation (the order is
	// re-derived per call) rather than about the task. The pool is finite and totally ordered, so the
	// claim is now false and the refusal is gone. These tests hold the replacement to the same standard
	// the listing walk is held to — pages must concatenate to the unpaged answer, exactly once each.

	// Six nodes that all match "alpha", with distinct bodies so the lexical leg has something to rank.
	Task SeedQueryable() => Seed("b", """
		[{"key":"q1","status":"Todo","title":"alpha one","body":"alpha material one"},
		 {"key":"q2","status":"Todo","title":"alpha two","body":"alpha material two"},
		 {"key":"q3","status":"Todo","title":"alpha three","body":"alpha material three"},
		 {"key":"q4","status":"Todo","title":"alpha four","body":"alpha material four"},
		 {"key":"q5","status":"Todo","title":"alpha five","body":"alpha material five"},
		 {"key":"q6","status":"Todo","title":"alpha six","body":"alpha material six"}]
		""");

	[Fact]
	public async Task PageWalk_WithQuery_ConcatenatesToTheUnpagedSelection()
	{
		await SeedQueryable();

		var whole = (await Search(q: "alpha", board: "b", limit: 100)).Nodes.Select(n => n.Key).ToList();
		whole.Should().HaveCount(6);

		var paged = await WalkAsync(c => Search(q: "alpha", board: "b", limit: 2, cursor: c));

		paged.Should().Equal(whole, "paging must change presentation only — never selection or order");
		paged.Should().OnlyHaveUniqueItems("a keyset seek re-serves nothing");
	}

	[Fact]
	public async Task PageWalk_WithQuery_PageSizeOfOne_StillCoversThePoolWithoutHoles()
	{
		await SeedQueryable();

		var whole = (await Search(q: "alpha", board: "b", limit: 100)).Nodes.Select(n => n.Key).ToList();
		var paged = await WalkAsync(c => Search(q: "alpha", board: "b", limit: 1, cursor: c));

		paged.Should().Equal(whole);
	}

	[Fact]
	public async Task SecondWalk_OverUnchangedData_ReproducesTheFirstWalkExactly()
	{
		// Requirement: «повторный проход при неизменных данных даёт тот же порядок».
		await SeedQueryable();

		var first = await WalkAsync(c => Search(q: "alpha", board: "b", limit: 2, cursor: c));
		var second = await WalkAsync(c => Search(q: "alpha", board: "b", limit: 2, cursor: c));

		second.Should().Equal(first);
	}

	[Fact]
	public async Task SecondPage_ReusesTheStoredPool_AndRunsNoSecondRerank()
	{
		// Requirement 5 observed end-to-end, and countable. It needs a WORKING rerank route: a pool that
		// fell back to RRF is deliberately never stored (no cross-encoder pass to save, and keeping it
		// would pin stale provenance), so a no-LLM service caches nothing — correct, but it cannot
		// demonstrate the saving. A local service keeps the LLM out of this class's other tests.
		await SeedQueryable();
		var llm = new PetBox.Tests.Memory.FlakyLlmClient();
		var cache = new SearchPoolCache();
		var tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory),
			new TagStore(_factory), new CommentService(_factory), llm: llm, poolCache: cache);

		var first = await TasksTools.SearchAsync(Http(), Flags(), tasks, Proj, "alpha", "b", null, null, null,
			false, null, null, null, 2, false, null, null, null);
		cache.Count.Should().Be(1, "page 1 materializes and stores the ranked pool");
		var passesAfterPageOne = llm.RerankCalls;
		passesAfterPageOne.Should().BeGreaterThan(0, "the cross-encoder decided this order");

		var second = await TasksTools.SearchAsync(Http(), Flags(), tasks, Proj, "alpha", "b", null, null, null,
			false, null, null, null, 2, false, null, null, first.NextCursor);

		second.Nodes.Should().NotBeEmpty();
		cache.Count.Should().Be(1, "page 2 must SERVE the stored pool, not rank a fresh one");
		llm.RerankCalls.Should().Be(passesAfterPageOne,
			"page 2 must not pay for the cross-encoder again — 3-4 seconds per page is what this avoids");
	}

	[Fact]
	public async Task ExhaustedSelection_SaysExhausted_AndIssuesNoCursor()
	{
		await SeedQueryable();

		var res = await Search(q: "alpha", board: "b", limit: 100);

		res.NextCursor.Should().BeNull();
		res.Stop.Should().Be("exhausted", "every matching node was ranked and served — there genuinely is no more");
		res.PoolBoundaryHint.Should().BeNull("nothing was left unlooked-at, so there is nothing to warn about");
	}

	[Fact]
	public async Task MidWalk_SaysMore_AndIssuesACursor()
	{
		await SeedQueryable();

		var res = await Search(q: "alpha", board: "b", limit: 2);

		res.Stop.Should().Be("more");
		res.NextCursor.Should().NotBeNull();
	}

	[Fact]
	public async Task QueryResponse_AlwaysStatesWhyItStopped()
	{
		// The anti-ambiguity rule: a consumer must never have to INFER the end from a missing cursor,
		// because "exhausted" and "pool-boundary" both omit it and mean different things.
		await SeedQueryable();

		foreach (var pageSize in new[] { 1, 2, 5, 100 })
			(await Search(q: "alpha", board: "b", limit: pageSize)).Stop
				.Should().BeOneOf("more", "exhausted", "pool-boundary");

		(await Search(board: "b")).Stop.Should().BeNull("a listing has no ranked pool, so it declares no pool stop");
	}

	[Fact]
	public async Task QueryResponse_DeclaresTheRankingDepth()
	{
		await SeedQueryable();

		var res = await Search(q: "alpha", board: "b", limit: 2);

		res.PoolLimit.Should().NotBeNull().And.BeGreaterThan(0,
			"the depth ranking was allowed to look is a NUMBER the caller can quote, not folklore");
	}

	[Fact]
	public async Task PoolRebuiltWithADifferentRankingMode_IsRefused_NotSpliced()
	{
		// tasks survived this by ACCIDENT: its token carries a score, and Advance's moved-row guard
		// compares sort values byte-for-byte, so a cross-encoder score never matched an RRF one. Nothing
		// stated that as an invariant, and a refactor dropping the score from the token — as memory
		// legitimately did — would have reopened it in silence. The order commitment makes it declared,
		// and this test is what keeps it declared.
		await SeedQueryable();
		var llm = new PetBox.Tests.Memory.FlakyLlmClient();
		var cache = new SearchPoolCache();
		var tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory),
			new TagStore(_factory), new CommentService(_factory), llm: llm, poolCache: cache);

		var first = await TasksTools.SearchAsync(Http(), Flags(), tasks, Proj, "alpha", "b", null, null, null,
			false, null, null, null, 2, false, null, null, null);
		first.NextCursor.Should().NotBeNull();

		// Evict the pool, then take the route down: page 2 rebuilds as plain RRF — same rows, different
		// order, nothing written, every data stamp still agreeing.
		for (var i = 0; i < 200; i++)
			cache.Put($"evict-{i}", new SearchPool([], 1, false, new SearchRetrievers(true, false, false)));
		llm.EmbedDown = true;

		var act = () => TasksTools.SearchAsync(Http(), Flags(), tasks, Proj, "alpha", "b", null, null, null,
			false, null, null, null, 2, false, null, null, first.NextCursor);

		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*ranked DIFFERENTLY*").WithMessage("*Your arguments are fine*");
	}

	// ── the order guard must fire on DRIFT, never on the resolve step ────────────────────────
	//
	// THE production defect (work/tasks-search-order-hash-nondeterministic): on the live box EVERY
	// tasks_search cursor was refused by the order guard, while the two calls returned byte-identical
	// rows and scores — the order hash was not a function of the order the caller was paging.
	//
	// The cause is that tasks pages a DIFFERENT pool from the one it hashed. `HybridCandidatesAsync`
	// returned the CORE search pool's hash — raw index docs, where a comment is addressed `c:<key>` and
	// an unresolvable doc still occupies a slot — but what gets cached, hydrated and walked is the
	// RESOLVED pool: comment docs mapped onto their owner NODE, duplicates dropped. Page 1 (a cache
	// miss) therefore minted a token carrying the core hash, and page 2 (a cache hit) recomputed the
	// resolved hash. Two different strings for one unchanged ordering → a guaranteed refusal.
	//
	// The existing q-mode walk tests cannot see it: with no LLM the pool is DegradedRrf and is never
	// cached, so both pages take the fresh branch and compare core-hash to core-hash; and with an LLM
	// but no comments the two pools happen to coincide row for row. A single comment in the pool is the
	// smallest corpus where the two addressings diverge — and on a real board comments are everywhere,
	// which is why prod failed on every query while every local walk was green.

	[Fact]
	public async Task PageWalk_WithQuery_WhenACommentIsInThePool_IsNotRefusedByTheOrderGuard()
	{
		await SeedQueryable();
		var comments = new CommentService(_factory);
		// A comment doc that MATCHES the query: it enters the core pool as `c:<key>` and leaves the
		// resolved pool as its owner node — the exact divergence the hash used to straddle.
		(await comments.AddAsync(Proj, "b", "q1", null, "alice", "alpha discussion of the material", null))
			.Applied.Should().BeTrue();

		var llm = new PetBox.Tests.Memory.FlakyLlmClient();
		var cache = new SearchPoolCache();
		var tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory),
			new TagStore(_factory), comments, llm: llm, poolCache: cache);

		Task<TaskSearchResultView> Page(string? cursor) => TasksTools.SearchAsync(
			Http(), Flags(), tasks, Proj, "alpha", "b", null, null, null,
			false, null, null, null, 2, false, null, null, cursor);

		var whole = await TasksTools.SearchAsync(Http(), Flags(), tasks, Proj, "alpha", "b", null, null, null,
			false, null, null, null, 100, false, null, null, null);
		var first = await Page(null);
		first.NextCursor.Should().NotBeNull("the walk has to start for the refusal to be reachable at all");
		cache.Count.Should().Be(1, "a reranked pool is cached — so page 2 takes the hydrate branch");

		var walked = await WalkAsync(Page);

		walked.Should().Equal(whole.Nodes.Select(n => n.Key),
			"nothing drifted between the pages — the guard must not stand in the way of an unchanged order");
		walked.Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public async Task TwoIdenticalQueries_MintTheSameCursor_EvenAcrossTheCacheBoundary()
	{
		// The observed prod symptom stated directly: call 1 misses the pool cache and call 2 hits it, the
		// answer is byte-identical — so the token, which commits to nothing but the query and the order,
		// must be byte-identical too. It was not: the two branches hashed two different pools.
		await SeedQueryable();
		var comments = new CommentService(_factory);
		await comments.AddAsync(Proj, "b", "q1", null, "alice", "alpha discussion of the material", null);

		var llm = new PetBox.Tests.Memory.FlakyLlmClient();
		var cache = new SearchPoolCache();
		var tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory),
			new TagStore(_factory), comments, llm: llm, poolCache: cache);

		Task<TaskSearchResultView> Call() => TasksTools.SearchAsync(
			Http(), Flags(), tasks, Proj, "alpha", "b", null, null, null,
			false, null, null, null, 2, false, null, null, null);

		var one = await Call();   // cache MISS — the pool is built and stored
		var two = await Call();   // cache HIT  — the pool is hydrated

		two.Nodes.Select(n => n.Key).Should().Equal(one.Nodes.Select(n => n.Key), "the answer itself is unchanged");
		two.NextCursor.Should().Be(one.NextCursor,
			"the same rows in the same order must mint the same token — an order hash that moves under an "
			+ "unchanged order is not a hash of the order");
	}

	// ── the deliberate refusals ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task Cursor_WithQuery_AfterTheBoardChanged_IsRefused_NotSilentlyRestarted()
	{
		// Requirement 4, and the sharpest difference from listing paging: in a RELEVANCE order a single
		// edit can move ANY row to ANY position, so continuing after one would splice two rankings. The
		// data version rides in the fingerprint precisely so this is a loud error.
		await SeedQueryable();
		var first = await Search(q: "alpha", board: "b", limit: 2);
		first.NextCursor.Should().NotBeNull();

		await Seed("b", """[{"key":"q7","status":"Todo","title":"alpha seven","body":"alpha material seven"}]""");

		var act = () => Search(q: "alpha", board: "b", limit: 2, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task Cursor_FromAQuery_IsRefused_AgainstADifferentQuery()
	{
		await SeedQueryable();
		var first = await Search(q: "alpha", board: "b", limit: 2);

		var act = () => Search(q: "material", board: "b", limit: 2, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task Cursor_FromAQuery_IsRefused_InListingMode()
	{
		// Dropping `q` changes both the selection and the ordering basis — the textbook silent-splice.
		await SeedQueryable();
		var first = await Search(q: "alpha", board: "b", limit: 2);

		var act = () => Search(board: "b", limit: 2, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task Cursor_WithGroupBy_IsRefused()
	{
		await Seed("b", """[{"key":"a","status":"Todo","title":"A","body":"x","tags":["area:mcp"]}]""");
		var token = new KeysetCursor("deadbeef", "0", "a", "b").Encode();

		var act = () => Search(board: "b", groupBy: "area", cursor: token);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*groupBy and cursor don't combine*");
	}

	[Fact]
	public async Task Cursor_FromADifferentSortAxis_IsRefused_NotSilentlyRestarted()
	{
		await Seed("b", """
			[{"key":"a","status":"Todo","title":"A","body":"x","priority":1},
			 {"key":"b","status":"Todo","title":"B","body":"x","priority":2}]
			""");
		var first = await Search(board: "b", limit: 1);
		first.NextCursor.Should().NotBeNull();

		// Same board, same rows — only the ORDER changed, which is exactly the case a lenient
		// decode would serve as a plausible-looking, wrong page.
		var act = () => Search(board: "b", sort: new SortInput { By = "title" }, limit: 1, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task Cursor_FromADifferentFilter_IsRefused()
	{
		await Seed("b", """
			[{"key":"a","status":"Todo","title":"A","body":"x","priority":1},
			 {"key":"b","status":"Todo","title":"B","body":"x","priority":2}]
			""");
		var first = await Search(board: "b", limit: 1);

		var act = () => Search(board: "b", status: ["Todo"], limit: 1, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task Cursor_ThatIsNotAToken_IsRefused()
	{
		await Seed("b", """[{"key":"a","status":"Todo","title":"A","body":"x"}]""");

		var act = () => Search(board: "b", cursor: "not-a-token!!");

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*OPAQUE*");
	}

	[Fact]
	public async Task Cursor_ValidBase64ButNotOurPayload_IsRefused()
	{
		await Seed("b", """[{"key":"a","status":"Todo","title":"A","body":"x"}]""");
		var junk = Convert.ToBase64String("hello there"u8.ToArray());

		var act = () => Search(board: "b", cursor: junk);

		await act.Should().ThrowAsync<ArgumentException>();
	}

	// ── knobs that are deliberately NOT part of the token identity ───────────────────────────

	[Fact]
	public async Task BodyLenAndLimit_MayVaryBetweenPages()
	{
		// They shape a page, not the sequence — binding them would reject valid walks for no gain.
		await Seed("b", """
			[{"key":"a","status":"Todo","title":"A","body":"xxxxxxxxxx","priority":1},
			 {"key":"b","status":"Todo","title":"B","body":"xxxxxxxxxx","priority":2},
			 {"key":"c","status":"Todo","title":"C","body":"xxxxxxxxxx","priority":3}]
			""");

		var first = await Search(board: "b", limit: 1, bodyLen: 0);
		var second = await Search(board: "b", limit: 2, bodyLen: -1, cursor: first.NextCursor);

		second.Nodes.Select(n => n.Key).Should().Equal("b", "c");
	}
}
