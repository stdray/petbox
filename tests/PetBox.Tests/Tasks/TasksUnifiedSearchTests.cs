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
	readonly CommentService _commentSvc;
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
		_commentSvc = new CommentService(_factory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory), new TagStore(_factory), _commentSvc);
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

	async Task<UpsertResultView> Seed(string board, string nodesJson)
	{
		// The tool layer no longer auto-vivifies a board (namespace-creation gate); create it
		// explicitly first, exactly as the old cold-upsert auto-vivify did (a simple board).
		if (!await _tasks.BoardExistsAsync(Proj, board))
			await _tasks.CreateBoardAsync(Proj, board, null, null, null);
		return await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, board, McpInputs.NodesJson(nodesJson));
	}

	Task<TaskSearchResultView> Search(
		string? q = null, string? board = null, string? under = null, string[]? status = null,
		string[]? keys = null, bool includeClosed = false, SortInput? sort = null,
		string? groupBy = null, int? bodyLen = null, int? limit = null, bool includeUrl = false,
		string[]? statusKind = null, string? commit = null) =>
		TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, q, board, under, status, keys,
			includeClosed, sort, groupBy, bodyLen, limit, includeUrl, commit: commit, statusKind: statusKind);

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

	// ---- keys: a SOFT node filter (miss-tolerant), slug|NodeId mixed ----

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

		// A miss is silently dropped (keys is a soft filter), never an error → empty result.
		var miss = await Search(board: "b", keys: ["ghost"]);
		miss.Nodes.Should().BeEmpty();
	}

	[Fact]
	public async Task Keys_SlugAcrossBoards_ResolvesProjectWide_AmbiguitySurfacesAllMatches()
	{
		await Seed("a", """[{"key":"unique-slug","status":"Todo","title":"U","body":"x"}]""");
		await Seed("a", """[{"key":"twin","status":"Todo","title":"T","body":"x"}]""");
		await Seed("b", """[{"key":"twin","status":"Todo","title":"T","body":"x"}]""");

		// Unambiguous slug resolves without a board...
		var res = await Search(keys: ["unique-slug"]);
		res.Nodes.Single().Board.Should().Be("a");

		// ...an ambiguous one (same slug on 2+ boards) surfaces ALL its matches — keys is a soft
		// filter, so a multi-board slug is not an error; each hit carries its own board.
		var dup = await Search(keys: ["twin"]);
		dup.Nodes.Select(n => n.Board).Should().BeEquivalentTo("a", "b");
	}

	// ---- status filter ----

	[Fact]
	public async Task Status_TerminalSlug_ExplicitAsk_NoIncludeClosedNeeded()
	{
		await Seed("b", """[{"key":"o","status":"Todo","title":"O","body":"x"},{"key":"d","status":"Todo","title":"D","body":"x"}]""");
		await Seed("b", """[{"key":"d","status":"Done","version":1}]""");

		var res = await Search(board: "b", status: ["Done"]);
		res.Nodes.Select(n => n.Key).Should().Equal("d");

		// An unknown status is silently dropped (soft filter); an all-unknown set → an empty result.
		var bogus = await Search(board: "b", status: ["bogus"]);
		bogus.Nodes.Should().BeEmpty();
	}

	// ---- statusKind facet + includeClosed deprecation (spec tasks-search-statuskind-facet) ----

	// The single place the deprecated includeClosed alias maps onto the statusKind vocabulary.
	// A naive includeClosed:true → [terminalcancel] would return ONLY closed and break callers;
	// the mapping below is the contract, proven directly on the pure resolver.
	[Fact]
	public void StatusKind_IncludeClosedAlias_MapsExactlyThreeCases()
	{
		// includeClosed:true → NEUTRAL (facet omitted, every kind) in either mode.
		TasksSearchDocs.ResolveStatusKindFacet(null, includeClosed: true, hasQuery: true).Should().BeNull();
		TasksSearchDocs.ResolveStatusKindFacet(null, includeClosed: true, hasQuery: false).Should().BeNull();
		// includeClosed:false + query → [open, terminalok] (a query only ever hid terminal-CANCEL).
		TasksSearchDocs.ResolveStatusKindFacet(null, includeClosed: false, hasQuery: true)
			.Should().BeEquivalentTo("open", "terminalok");
		// includeClosed:false + listing → [open] (a listing hid ALL terminal).
		TasksSearchDocs.ResolveStatusKindFacet(null, includeClosed: false, hasQuery: false)
			.Should().BeEquivalentTo("open");
		// An explicit statusKind WINS over the alias (validated + lowercased), in either mode.
		TasksSearchDocs.ResolveStatusKindFacet(["TerminalCancel"], includeClosed: false, hasQuery: false)
			.Should().BeEquivalentTo("terminalcancel");
	}

	[Fact]
	public void StatusKind_UnknownValue_IsError() =>
		FluentActions.Invoking(() => TasksSearchDocs.ResolveStatusKindFacet(["closed"], false, true))
			.Should().Throw<ArgumentException>().WithMessage("*closed*status kind*");

	// ---- effective statusKind echo (spec search-echo-effective-statuskind-filter) ----
	//
	// The DEFAULT visibility facet must be OBSERVABLE in the response, not a silent mechanism —
	// the response's `effectiveStatusKind` echoes EXACTLY what TasksSearchDocs.ResolveStatusKindFacet
	// resolved (one authority, never a recomputed parallel value).

	// A default QUERY narrows to open+terminalok (the frame invariant: accepted/Done stay findable) —
	// that default must be echoed back, not silently applied.
	[Fact]
	public async Task Query_Default_EchoesEffectiveStatusKind_OpenTerminalOk()
	{
		await Seed("b", """[{"key":"echo-q","status":"Todo","title":"echoquery marker","body":"x"}]""");

		var res = await Search(q: "echoquery");

		res.EffectiveStatusKind.Should().BeEquivalentTo(new[] { "open", "terminalok" });
	}

	// A default LISTING narrows to open ONLY — a different default than query mode — and it too
	// must be echoed.
	[Fact]
	public async Task Listing_Default_EchoesEffectiveStatusKind_Open()
	{
		await Seed("b", """[{"key":"echo-l","status":"Todo","title":"L","body":"x"}]""");

		var res = await Search(board: "b");

		res.EffectiveStatusKind.Should().BeEquivalentTo(new[] { "open" });
	}

	// An explicit statusKind WINS over the default and is echoed back exactly as resolved
	// (validated/normalized/deduped), in BOTH modes.
	[Fact]
	public async Task ExplicitStatusKind_EchoedAsResolved_BothModes()
	{
		await Seed("b", """[{"key":"echo-e","status":"Todo","title":"echoexplicit marker","body":"x"}]""");

		var listing = await Search(board: "b", statusKind: ["TerminalCancel", "TerminalCancel"]);
		listing.EffectiveStatusKind.Should().BeEquivalentTo(new[] { "terminalcancel" }); // normalized + deduped

		var query = await Search(q: "echoexplicit", statusKind: ["Open"]);
		query.EffectiveStatusKind.Should().BeEquivalentTo(new[] { "open" });
	}

	// The deprecated includeClosed alias maps onto the SAME resolver the echo reads: includeClosed:true
	// is NEUTRAL (no facet applied — every kind) and echoes null (there is no "effective narrowing" to
	// report); includeClosed:false reproduces the mode default and is echoed explicitly, exactly like
	// the no-argument default above.
	[Fact]
	public async Task IncludeClosedAlias_MappedAndEchoed()
	{
		await Seed("b", """[{"key":"echo-ic","status":"Todo","title":"echoinclosed marker","body":"x"}]""");

		(await Search(board: "b", includeClosed: true)).EffectiveStatusKind.Should().BeNull();
		(await Search(q: "echoinclosed", includeClosed: true)).EffectiveStatusKind.Should().BeNull();
		(await Search(board: "b", includeClosed: false)).EffectiveStatusKind.Should().BeEquivalentTo(new[] { "open" });
		(await Search(q: "echoinclosed", includeClosed: false)).EffectiveStatusKind
			.Should().BeEquivalentTo(new[] { "open", "terminalok" });
	}

	// HARD FRAME INVARIANT: accepted/Done (terminal-OK) MUST be found by a DEFAULT query — this is
	// what search-before-rework and the ideaRef gate stand on. No includeClosed, no statusKind.
	[Fact]
	public async Task Query_Default_FindsTerminalOk_FrameInvariant()
	{
		await Seed("b", """[{"key":"axolotl-node","status":"Todo","title":"axolotl marker","body":"axolotl body"}]""");
		await Seed("b", """[{"key":"axolotl-node","status":"Done","version":1}]"""); // Done = terminalok on simple

		var res = await Search(q: "axolotl"); // default: no includeClosed, no statusKind
		res.Nodes.Select(n => n.Key).Should().Equal("axolotl-node");
		res.Nodes[0].Status.Should().Be("Done");
	}

	// A default query hides terminal-CANCEL; the statusKind facet surfaces it — no boolean closed.
	[Fact]
	public async Task Query_Default_HidesTerminalCancel_StatusKindSurfacesIt()
	{
		await Seed("b", """
			[{"key":"narwhal-open","status":"Todo","title":"narwhal one","body":"narwhal"},
			 {"key":"narwhal-gone","status":"Todo","title":"narwhal two","body":"narwhal"}]
			""");
		await Seed("b", """[{"key":"narwhal-gone","status":"Cancelled","version":1}]"""); // terminalcancel

		(await Search(q: "narwhal")).Nodes.Select(n => n.Key).Should().Equal("narwhal-open");
		(await Search(q: "narwhal", statusKind: ["terminalcancel"])).Nodes.Select(n => n.Key).Should().Equal("narwhal-gone");
		// The union is reachable by naming both kinds — the facet is a SET.
		(await Search(q: "narwhal", statusKind: ["open", "terminalcancel"])).Nodes.Select(n => n.Key)
			.Should().BeEquivalentTo("narwhal-open", "narwhal-gone");
	}

	// An explicit statusKind OVERRIDES the deprecated includeClosed (first-class wins).
	[Fact]
	public async Task Query_StatusKind_FirstClass_OverridesIncludeClosed()
	{
		await Seed("b", """
			[{"key":"gecko-open","status":"Todo","title":"gecko one","body":"gecko"},
			 {"key":"gecko-done","status":"Todo","title":"gecko two","body":"gecko"}]
			""");
		await Seed("b", """[{"key":"gecko-done","status":"Cancelled","version":1}]""");

		// includeClosed:true would widen to all, but statusKind:[open] wins → only the open node.
		var res = await Search(q: "gecko", includeClosed: true, statusKind: ["open"]);
		res.Nodes.Select(n => n.Key).Should().Equal("gecko-open");
	}

	// Listing and query evaluate the SAME statusKind facet with the SAME semantics (parity).
	[Fact]
	public async Task StatusKind_ListingAndQuery_SameFacetSemantics()
	{
		await Seed("b", """
			[{"key":"quokka-open","status":"Todo","title":"quokka one","body":"quokka"},
			 {"key":"quokka-done","status":"Todo","title":"quokka two","body":"quokka"}]
			""");
		await Seed("b", """[{"key":"quokka-done","status":"Done","version":1}]"""); // terminalok

		// statusKind:[open] excludes the Done node in BOTH modes.
		(await Search(board: "b", statusKind: ["open"])).Nodes.Select(n => n.Key).Should().Equal("quokka-open");
		(await Search(q: "quokka", statusKind: ["open"])).Nodes.Select(n => n.Key).Should().Equal("quokka-open");
		// statusKind:[terminalok] keeps ONLY the Done node in BOTH modes.
		(await Search(board: "b", statusKind: ["terminalok"])).Nodes.Select(n => n.Key).Should().Equal("quokka-done");
		(await Search(q: "quokka", statusKind: ["terminalok"])).Nodes.Select(n => n.Key).Should().Equal("quokka-done");
	}

	// Listing evaluates the statusKind facet against the SAME опорный слой (search_meta) the query
	// leg uses — one authority, identical membership in both modes — while KEEPING the entity default
	// ORDER (priority then key) that is a listing specialization, not an опорный-слой property.
	[Fact]
	public async Task Listing_StatusKindParity_SharedAuthority_KeepsEntityOrder()
	{
		await Seed("b", """
			[{"key":"z-open","status":"Todo","title":"z","body":"quux","priority":30},
			 {"key":"a-open","status":"Todo","title":"a","body":"quux","priority":10},
			 {"key":"m-done","status":"Todo","title":"m","body":"quux","priority":20},
			 {"key":"q-cancel","status":"Todo","title":"q","body":"quux","priority":5}]
			""");
		await Seed("b", """[{"key":"m-done","status":"Done","version":1}]""");     // terminalok
		await Seed("b", """[{"key":"q-cancel","status":"Cancelled","version":1}]"""); // terminalcancel

		// Default listing ORDER stays the entity specialization: priority (10 before 30), then key.
		(await Search(board: "b", statusKind: ["open"])).Nodes.Select(n => n.Key).Should().Equal("a-open", "z-open");

		// Mixed set: listing and query select the SAME members from the shared authority (parity).
		var mixed = new[] { "open", "terminalcancel" };
		var listing = (await Search(board: "b", statusKind: mixed)).Nodes.Select(n => n.Key).ToHashSet();
		var query = (await Search(q: "quux", board: "b", statusKind: mixed)).Nodes.Select(n => n.Key).ToHashSet();
		listing.Should().BeEquivalentTo(new[] { "a-open", "z-open", "q-cancel" }); // terminalok m-done excluded in both
		query.Should().BeEquivalentTo(listing);
	}

	// ---- presentation tiers (spec tasks-search-statuskind-presentation-tiers) ----

	// The tier is a STABLE PARTITION over the fused relevance order (open → terminalok →
	// terminalcancel): it demotes terminal nodes but preserves the relevance order WITHIN each tier
	// (not a re-sort), hides nothing (not a cliff), and never folds terminalok in with terminalcancel.
	[Fact]
	public async Task PresentationTiers_StablePartition_NotResort_NotCliff()
	{
		await Seed("b", """
			[{"key":"op-a","status":"Todo","title":"pangolin a","body":"pangolin pangolin pangolin"},
			 {"key":"op-b","status":"Todo","title":"pangolin b","body":"pangolin"},
			 {"key":"ok-c","status":"Todo","title":"pangolin c","body":"pangolin pangolin"},
			 {"key":"can-d","status":"Todo","title":"pangolin d","body":"pangolin pangolin"}]
			""");
		await Seed("b", """[{"key":"ok-c","status":"Done","version":1}]"""); // terminalok
		await Seed("b", """[{"key":"can-d","status":"Cancelled","version":1}]"""); // terminalcancel

		// The open tier, in pure relevance order (op-a is more keyword-dense than op-b).
		var openOrder = (await Search(q: "pangolin", statusKind: ["open"])).Nodes.Select(n => n.Key).ToArray();
		openOrder.Should().Equal("op-a", "op-b");

		// All three tiers present: the partition reorders, it never drops (not a cliff).
		var all = (await Search(q: "pangolin", statusKind: ["open", "terminalok", "terminalcancel"]))
			.Nodes.Select(n => n.Key).ToList();
		all.Should().BeEquivalentTo(new[] { "op-a", "op-b", "ok-c", "can-d" });

		// Partition-not-resort: the open tier leads, in the SAME relevance order it had alone (a global
		// re-sort would have reshuffled it) — and the terminal tiers follow, terminalok BEFORE
		// terminalcancel (accepted/Done never folded in with rejected/cancelled).
		all.Take(2).Should().Equal(openOrder);              // open tier first, relevance order preserved
		all.IndexOf("ok-c").Should().BeLessThan(all.IndexOf("can-d")); // terminalok tier before terminalcancel
		all.IndexOf("op-b").Should().BeLessThan(all.IndexOf("ok-c"));  // whole open tier above any terminal
	}

	// tasks-search-drop-terminal-default (exact leg): the exact-identifier leg is SUBJECT TO the
	// statusKind facet — no terminal override. A default exact lookup hides a terminal-CANCEL node;
	// statusKind:[terminalcancel] surfaces it; a terminal-OK exact hit stays findable by default.
	[Fact]
	public async Task ExactLeg_SubjectToStatusKindFacet_DefaultHidesCancel_NamedSurfaces()
	{
		await Seed("b", """[{"key":"exact-gone","status":"Todo","title":"exact gone","body":"body"}]""");
		await Seed("b", """[{"key":"exact-gone","status":"Cancelled","version":1}]"""); // terminalcancel

		// The query IS the slug (identity leg). Default lookup obeys the facet → the cancelled node is hidden.
		(await Search(q: "exact-gone")).Nodes.Should().BeEmpty();
		// statusKind:[terminalcancel] surfaces it by exact id.
		(await Search(q: "exact-gone", board: "b", statusKind: ["terminalcancel"])).Nodes.Select(n => n.Key)
			.Should().Equal("exact-gone");

		// A terminal-OK exact hit stays findable by a DEFAULT lookup (frame invariant).
		await Seed("b", """[{"key":"exact-ok","status":"Todo","title":"exact ok","body":"body"}]""");
		await Seed("b", """[{"key":"exact-ok","status":"Done","version":1}]""");
		(await Search(q: "exact-ok")).Nodes.Select(n => n.Key).Should().Equal("exact-ok");
	}

	// ---- entity predicates (spec tasks-search-entity-predicates-under-commit) ----

	// `under` (part_of subtree) and `commit` are predicates the опорный слой cannot express, applied
	// at the re-filter step. They NARROW the already-faceted pool — they must NOT resurrect a node the
	// statusKind facet excluded (no selecting past the опорный слой). Orthogonal to the facet: naming
	// the excluded kind restores it, proving the entity predicate itself is a pure narrowing filter.
	[Fact]
	public async Task EntityPredicate_Under_DoesNotSelectPastStatusKindFacet()
	{
		await Seed("b", """
			[{"key":"burrow","status":"Todo","title":"wombat root","body":"wombat"},
			 {"key":"kid-open","status":"Todo","title":"wombat kept","body":"wombat","partOf":"burrow"},
			 {"key":"kid-gone","status":"Todo","title":"wombat lost","body":"wombat","partOf":"burrow"}]
			""");
		await Seed("b", """[{"key":"kid-gone","status":"Cancelled","version":1}]"""); // terminalcancel

		// Default query under the subtree (the root is in its own subtree): the entity predicate keeps
		// the subtree, the facet still hides the cancelled child — under did NOT select past the опорный слой.
		(await Search(q: "wombat", under: "burrow")).Nodes.Select(n => n.Key)
			.Should().BeEquivalentTo("burrow", "kid-open");
		// Orthogonal: name terminalcancel and the SAME under predicate now yields the cancelled child.
		(await Search(q: "wombat", under: "burrow", statusKind: ["terminalcancel"])).Nodes.Select(n => n.Key)
			.Should().BeEquivalentTo("kid-gone");
	}

	[Fact]
	public async Task EntityPredicate_Commit_DoesNotSelectPastStatusKindFacet()
	{
		await Seed("b", """[{"key":"shipped","status":"Todo","title":"dingo work","body":"dingo","commits":["deadbee1234567"]}]""");
		await Seed("b", """[{"key":"shipped","status":"Cancelled","version":1}]"""); // terminalcancel, still carries the commit

		// Default query + commit: the facet hides the cancelled node — commit did not select past it.
		(await Search(q: "dingo", commit: "deadbee")).Nodes.Should().BeEmpty();
		// Naming terminalcancel restores it — commit is a pure narrowing re-filter, orthogonal to the facet.
		(await Search(q: "dingo", commit: "deadbee", statusKind: ["terminalcancel"])).Nodes.Select(n => n.Key)
			.Should().Equal("shipped");
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
		await new RelationStore(_factory).CreateAsync(Proj, "task_spec", featId, specId);
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

	[Fact]
	public async Task Query_CommentMatch_LeanWireRow_CarriesMatchedIn() // spec tasks-search-comments
	{
		await Seed("b", """[{"key":"host","status":"Todo","title":"Host","body":"plain node body","priority":10}]""");
		var nodeId = (await _tasks.GetAsync(Proj, "b", includeClosed: false)).Nodes.First(n => n.Key == "host").NodeId;
		await _commentSvc.AddAsync(Proj, "b", nodeId, null, "author", "dugong note lives in this comment", null);

		// The token is only in the comment — the OWNER node row comes back, marked on the wire.
		var res = await Search(q: "dugong");
		res.Nodes.Should().ContainSingle();
		res.Nodes[0].Key.Should().Be("host");
		res.Nodes[0].MatchedIn.Should().Be("comment");
		res.Nodes[0].Retriever.Should().Be("lexical");
	}

	// Regression (orphan-search-docs 500): deleting a board bulk-drops its PlanNodes rows but must
	// also purge its (Scope=project, Type=board) search docs. Pre-fix the orphan FTS doc kept
	// matching, then HybridCandidatesAsync called GetAsync on the vanished board and threw
	// InvalidOperationException ("task board '...' not found") — surfacing as a 500 on /ui/search.
	[Fact]
	public async Task DeleteBoard_PurgesSearchDocs_SearchDoesNotThrow()
	{
		await Seed("doomed", """[{"key":"n","status":"Todo","title":"zqxprovisionruntime marker","body":"zqxprovisionruntime body"}]""");

		// Sanity: the node is indexed and the unique term selects it.
		(await Search(q: "zqxprovisionruntime")).Nodes.Select(n => n.Key).Should().Equal("n");

		// Drop the whole board — its rows AND its search docs must go.
		(await _tasks.DeleteBoardAsync(Proj, "doomed")).Should().BeTrue();

		// The regression: this search must NOT throw and must return no orphan hit.
		var after = await Search(q: "zqxprovisionruntime");
		after.Nodes.Should().BeEmpty();
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
