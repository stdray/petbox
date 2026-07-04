using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// The unified tasks_search verb (spec uniform-entity-verbs v2): ONE read tool where
// list = search without `q` and relevance is a sort option only with `q`. Covers both
// modes (board-scoped and project-wide listing, hybrid query with retriever provenance),
// the shared predicates (status, keys slug|NodeId mixed, terminal addressing), the
// server-side sort axes, and the mode/parameter guards (relevance-without-q, groupBy+q).
// These fixtures run without an embedder, so query mode is lexical-only by construction.
public sealed class TasksUnifiedSearchTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public TasksUnifiedSearchTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-unifiedsearch-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(id) };
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

	Task<UpsertResultView> Seed(string board, string nodesJson) =>
		TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, board, McpInputs.NodesJson(nodesJson));

	Task<TaskSearchResultView> Search(
		string? q = null, string? board = null, string? under = null, string[]? status = null,
		string[]? keys = null, bool includeClosed = false, SortInput? sort = null,
		string? groupBy = null, int? bodyLen = null, int? limit = null, bool includeUrl = false) =>
		TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, q, board, under, status, keys,
			includeClosed, sort, groupBy, bodyLen, limit, includeUrl);

	// ---- listing mode (no q) ----

	[Fact]
	public async Task Listing_BoardScoped_CarriesBoardContext_AndTreeFields()
	{
		await Seed("b", """
			[{"key":"root","status":"Todo","title":"Root","body":"r","priority":10},
			 {"key":"leaf","status":"Todo","title":"Leaf","body":"l","partOf":"root","priority":20}]
			""");

		var res = await Search(board: "b");

		// Board context: the former tasks.get header fields.
		res.Board.Should().Be("b");
		res.Kind.Should().Be("simple");
		res.SpecBoard.Should().BeNull();
		res.CurrentVersion.Should().BeGreaterThan(0);
		res.Retrievers.Should().BeNull(); // a listing involves no retriever
		res.GroupBy.Should().BeNull();

		// Default order = priority then key; rows carry the tree projection + their board.
		res.Nodes.Select(n => n.Key).Should().Equal("root", "leaf");
		var leaf = res.Nodes.Single(n => n.Key == "leaf");
		leaf.Board.Should().Be("b");
		leaf.ParentSlug.Should().Be("root");
		leaf.Depth.Should().Be(1);
		leaf.Body.Should().Be("l"); // short body returned whole under the default snippet
	}

	[Fact]
	public async Task Listing_ProjectWide_SpansBoards_NoBoardContext()
	{
		await Seed("a", """[{"key":"on-a","status":"Todo","title":"A","body":"x","priority":2}]""");
		await Seed("b", """[{"key":"on-b","status":"Todo","title":"B","body":"x","priority":1}]""");

		var res = await Search();

		// Rows span boards (each names its board), ordered by priority then key project-wide.
		res.Nodes.Select(n => (n.Key, n.Board)).Should().Equal(("on-b", "b"), ("on-a", "a"));
		// No single board in scope → no board context.
		res.Board.Should().BeNull();
		res.Kind.Should().BeNull();
		res.CurrentVersion.Should().BeNull();
	}

	[Fact]
	public async Task Listing_HidesTerminal_IncludeClosedWidens()
	{
		await Seed("b", """[{"key":"open-one","status":"Todo","title":"O","body":"x"},{"key":"done-one","status":"Todo","title":"D","body":"x"}]""");
		await Seed("b", """[{"key":"done-one","status":"Done","version":1}]""");

		(await Search(board: "b")).Nodes.Select(n => n.Key).Should().Equal("open-one");
		(await Search(board: "b", includeClosed: true)).Nodes.Select(n => n.Key)
			.Should().BeEquivalentTo("open-one", "done-one");
	}

	// ---- keys: explicit addressing, slug|NodeId mixed ----

	[Fact]
	public async Task Keys_MixedSlugAndNodeId_TerminalNodeReturnedWithoutIncludeClosed()
	{
		var up = await Seed("b", """
			[{"key":"alpha","status":"Todo","title":"A","body":"x"},
			 {"key":"beta","status":"Todo","title":"B","body":"x"},
			 {"key":"gamma","status":"Todo","title":"C","body":"x"}]
			""");
		var betaId = up.Added.Single(n => n.Key == "beta").NodeId;
		await Seed("b", """[{"key":"beta","status":"Done","version":1}]""");

		// One slug + one 32-hex NodeId, mixed; the addressed terminal node comes back too.
		var res = await Search(board: "b", keys: ["alpha", betaId]);
		res.Nodes.Select(n => n.Key).Should().BeEquivalentTo("alpha", "beta");
		res.Nodes.Single(n => n.Key == "beta").Status.Should().Be("Done");

		// A miss is a clear error, never a silently empty answer.
		var miss = () => Search(board: "b", keys: ["ghost"]);
		(await miss.Should().ThrowAsync<ArgumentException>()).WithMessage("*ghost*");
	}

	[Fact]
	public async Task Keys_SlugAcrossBoards_ResolvesProjectWide_AmbiguityRejected()
	{
		await Seed("a", """[{"key":"unique-slug","status":"Todo","title":"U","body":"x"}]""");
		await Seed("a", """[{"key":"twin","status":"Todo","title":"T","body":"x"}]""");
		await Seed("b", """[{"key":"twin","status":"Todo","title":"T","body":"x"}]""");

		// Unambiguous slug resolves without a board...
		var res = await Search(keys: ["unique-slug"]);
		res.Nodes.Single().Board.Should().Be("a");

		// ...an ambiguous one names the boards and demands a NodeId.
		var dup = () => Search(keys: ["twin"]);
		(await dup.Should().ThrowAsync<ArgumentException>()).WithMessage("*ambiguous*twin*");
	}

	// ---- status filter ----

	[Fact]
	public async Task Status_TerminalSlug_ExplicitAsk_NoIncludeClosedNeeded()
	{
		await Seed("b", """[{"key":"o","status":"Todo","title":"O","body":"x"},{"key":"d","status":"Todo","title":"D","body":"x"}]""");
		await Seed("b", """[{"key":"d","status":"Done","version":1}]""");

		var res = await Search(board: "b", status: ["Done"]);
		res.Nodes.Select(n => n.Key).Should().Equal("d");

		var bogus = () => Search(board: "b", status: ["bogus"]);
		(await bogus.Should().ThrowAsync<ArgumentException>()).WithMessage("*bogus*not a status*");
	}

	// ---- query mode (q) ----

	[Fact]
	public async Task Query_SelectsByRelevance_CarriesRetrievers_AndBoardOnRows()
	{
		await Seed("a", """[{"key":"hit-a","status":"Todo","title":"walrus note","body":"the walrus keyword"}]""");
		await Seed("b", """
			[{"key":"hit-b","status":"Todo","title":"walrus too","body":"another walrus"},
			 {"key":"miss","status":"Todo","title":"unrelated","body":"nothing here"}]
			""");

		var res = await Search(q: "walrus");

		res.Nodes.Select(n => n.Key).Should().BeEquivalentTo("hit-a", "hit-b");
		res.Nodes.Should().OnlyContain(n => n.Board == "a" || n.Board == "b");
		// Provenance: lexical ran (no embedder wired → semantic honestly false, not degraded).
		res.Retrievers.Should().NotBeNull();
		res.Retrievers!.Lexical.Should().BeTrue();
		res.Retrievers!.Semantic.Should().BeFalse();
		res.Retrievers!.Degraded.Should().BeFalse();
	}

	[Fact]
	public async Task Query_StatusFilter_IsAPredicateOverTheSelectedSet()
	{
		await Seed("b", """
			[{"key":"pelican-todo","status":"Todo","title":"pelican one","body":"pelican"},
			 {"key":"pelican-doing","status":"InProgress","title":"pelican two","body":"pelican"}]
			""");

		var res = await Search(q: "pelican", status: ["InProgress"]);

		res.Nodes.Select(n => n.Key).Should().Equal("pelican-doing");
		res.Retrievers.Should().NotBeNull(); // still a query answer, with provenance
	}

	[Fact]
	public async Task Query_Limit_CapsRows()
	{
		await Seed("b", """
			[{"key":"heron-1","status":"Todo","title":"heron","body":"heron"},
			 {"key":"heron-2","status":"Todo","title":"heron","body":"heron"},
			 {"key":"heron-3","status":"Todo","title":"heron","body":"heron"}]
			""");

		(await Search(q: "heron", limit: 1)).Nodes.Should().HaveCount(1);
		(await Search(q: "heron")).Nodes.Should().HaveCount(3); // default 20 covers all
	}

	// A NON-vacuous node — it HAS a parent (partOf), a spec link (task_spec relation), commits and
	// tags — used by both the lean q-mode and the enriched listing-mode assertions below.
	async Task<(string FeatId, string SpecId)> SeedEnrichedNode()
	{
		var up = await Seed("b", """
			[{"key":"parent","status":"Todo","title":"parent","body":"root"},
			 {"key":"spec-target","status":"Todo","title":"spec target","body":"target"},
			 {"key":"marmot-feat","status":"Todo","title":"marmot feature","body":"the marmot keyword","partOf":"parent","commits":["abc1234"],"tags":["area:search"]}]
			""");
		var featId = up.Added.Single(n => n.Key == "marmot-feat").NodeId;
		var specId = up.Added.Single(n => n.Key == "spec-target").NodeId;
		// Direct task_spec edge → the node carries a `spec` link on ANY board kind (the listing
		// enrichment), so the q-mode drop of it below is not vacuous.
		await new RelationStore(_db).CreateAsync(Proj, "task_spec", featId, specId);
		return (featId, specId);
	}

	[Fact]
	public async Task Query_RowsAreLean_EnrichmentOmitted() // spec search-lean-rows
	{
		var (featId, _) = await SeedEnrichedNode();

		var res = await Search(q: "marmot", includeUrl: true);
		var row = res.Nodes.Single(n => n.Key == "marmot-feat");

		// Lean cut: a relevance row carries nothing beyond what picks the entity — the
		// enrichment (parent/depth/delivery/spec/links/commits/priority) is omitted (null → dropped).
		row.ParentNodeId.Should().BeNull();
		row.ParentSlug.Should().BeNull();
		row.Depth.Should().BeNull();
		row.Delivery.Should().BeNull();
		row.Spec.Should().BeNull();
		row.BlockedBy.Should().BeNull();
		row.LinkedTasks.Should().BeNull();
		row.Supersedes.Should().BeNull();
		row.RenamedFrom.Should().BeNull();
		row.Commits.Should().BeNull();
		row.Priority.Should().BeNull();

		// Kept: identity/title/status/type/tags/version + score/retriever (+ url when asked).
		row.Key.Should().Be("marmot-feat");
		row.NodeId.Should().Be(featId);
		row.Board.Should().Be("b");
		row.Status.Should().Be("Todo");
		row.Type.Should().NotBeNull();
		row.Title.Should().Be("marmot feature");
		row.Tags.Should().Contain("area:search");
		row.Version.Should().BeGreaterThan(0);
		row.Score.Should().NotBeNull();
		row.Retriever.Should().Be("lexical");
		row.Url.Should().NotBeNull();
	}

	[Fact]
	public async Task Listing_KeepsEnrichment_LeanCutIsQueryModeOnly() // spec search-lean-rows
	{
		var (_, specId) = await SeedEnrichedNode();

		// The SAME node in listing mode (no q) keeps its full enrichment — the lean cut is q-mode only.
		var res = await Search(board: "b");
		var row = res.Nodes.Single(n => n.Key == "marmot-feat");

		row.ParentSlug.Should().Be("parent");
		row.ParentNodeId.Should().NotBeNull();
		row.Depth.Should().Be(1);
		row.Commits.Should().BeEquivalentTo("abc1234");
		row.Priority.Should().NotBeNull();
		row.Spec.Should().NotBeNull();
		row.Spec!.Should().ContainSingle(l => l.NodeId == specId);
		// A listing runs no relevance leg → no per-row provenance.
		row.Score.Should().BeNull();
		row.Retriever.Should().BeNull();
	}

	// ---- sort ----

	[Fact]
	public async Task Sort_Title_And_PriorityDesc_ReorderTheListing()
	{
		await Seed("b", """
			[{"key":"n1","status":"Todo","title":"zebra","body":"x","priority":1},
			 {"key":"n2","status":"Todo","title":"aardvark","body":"x","priority":2},
			 {"key":"n3","status":"Todo","title":"mongoose","body":"x","priority":3}]
			""");

		(await Search(board: "b", sort: new SortInput { By = "title" }))
			.Nodes.Select(n => n.Title).Should().Equal("aardvark", "mongoose", "zebra");
		(await Search(board: "b", sort: new SortInput { By = "priority", Desc = true }))
			.Nodes.Select(n => n.Key).Should().Equal("n3", "n2", "n1");
	}

	[Fact]
	public async Task Sort_CreatedAndUpdated_UseTemporalColumns()
	{
		await Seed("b", """[{"key":"older","status":"Todo","title":"older","body":"x","priority":2}]""");
		await Task.Delay(30);
		await Seed("b", """[{"key":"newer","status":"Todo","title":"newer","body":"x","priority":1}]""");
		await Task.Delay(30);
		// Touch the OLDER node so its Updated overtakes the newer node's.
		await Seed("b", """[{"key":"older","body":"edited","version":1}]""");

		(await Search(board: "b", sort: new SortInput { By = "created" }))
			.Nodes.Select(n => n.Key).Should().Equal("older", "newer");
		(await Search(board: "b", sort: new SortInput { By = "created", Desc = true }))
			.Nodes.Select(n => n.Key).Should().Equal("newer", "older");
		(await Search(board: "b", sort: new SortInput { By = "updated", Desc = true }))
			.Nodes.Select(n => n.Key).Should().Equal("older", "newer");
	}

	[Fact]
	public async Task Sort_WithQuery_ReordersWithinTheSelectedSet()
	{
		await Seed("b", """
			[{"key":"osprey-b","status":"Todo","title":"beta osprey","body":"osprey"},
			 {"key":"osprey-a","status":"Todo","title":"alpha osprey","body":"osprey"},
			 {"key":"other","status":"Todo","title":"unrelated","body":"nothing"}]
			""");

		var res = await Search(q: "osprey", sort: new SortInput { By = "title" });

		// Selection stays relevance-driven (only osprey nodes), presentation is title order.
		res.Nodes.Select(n => n.Title).Should().Equal("alpha osprey", "beta osprey");
	}

	[Fact]
	public async Task Sort_Relevance_WithoutQuery_IsAClearError()
	{
		await Seed("b", """[{"key":"n","status":"Todo","title":"N","body":"x"}]""");

		var act = () => Search(board: "b", sort: new SortInput { By = "relevance" });
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*relevance*needs a query*");
	}

	[Fact]
	public async Task Sort_UnknownAxis_IsRejectedNamingTheValidOnes()
	{
		var act = () => Search(board: "b", sort: new SortInput { By = "bogus" });
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*bogus*").WithMessage("*priority|created|updated|title|relevance*");
	}

	// ---- groupBy: the projection mode ----

	[Fact]
	public async Task GroupBy_ReturnsProjection_InTheUnifiedShape()
	{
		await Seed("b", """[{"key":"a","status":"Todo","title":"A","body":"x","tags":["area:ui"]}]""");

		var res = await Search(board: "b", groupBy: "area");

		res.Nodes.Should().BeEmpty();
		res.GroupBy.Should().Equal("area");
		res.Groups.Should().NotBeNull();
		res.Groups!.SelectMany(g => g.NodeKeys).Should().Contain("a");
		res.Board.Should().Be("b");
	}

	[Fact]
	public async Task GroupBy_WithQuery_OrWithoutBoard_IsRejected()
	{
		await Seed("b", """[{"key":"a","status":"Todo","title":"A","body":"x"}]""");

		var withQ = () => Search(q: "a", board: "b", groupBy: "area");
		(await withQ.Should().ThrowAsync<ArgumentException>()).WithMessage("*groupBy and q*");

		var noBoard = () => Search(groupBy: "area");
		(await noBoard.Should().ThrowAsync<ArgumentException>()).WithMessage("*groupBy needs a board*");
	}

	// ---- under: subtree predicate, both modes ----

	[Fact]
	public async Task Under_ScopesListing_AndQuery_ToTheSubtree()
	{
		await Seed("b", """
			[{"key":"apex","status":"Todo","title":"Apex","body":"puffin here"},
			 {"key":"apex-leaf","status":"Todo","title":"Leaf","body":"puffin too","partOf":"apex"},
			 {"key":"stray","status":"Todo","title":"Stray","body":"puffin outside"}]
			""");

		(await Search(board: "b", under: "apex")).Nodes.Select(n => n.Key)
			.Should().BeEquivalentTo("apex", "apex-leaf");
		(await Search(q: "puffin", under: "apex")).Nodes.Select(n => n.Key)
			.Should().BeEquivalentTo("apex", "apex-leaf"); // "stray" matched but is outside the subtree
	}

	// ---- includeUrl rides through ----

	[Fact]
	public async Task IncludeUrl_EmitsCanonicalSlugPermalinks()
	{
		await Seed("b", """[{"key":"n","status":"Todo","title":"N","body":"x"}]""");

		var res = await Search(board: "b", includeUrl: true);
		res.Nodes.Single().Url.Should().Be($"https://box.test/ui/ws/{Proj}/tasks/b/n");
	}
}
