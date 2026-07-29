using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// tasks-ui-pages-getting-slower: board-page-cost fixed the BOARD list page's N+1 (relations) and
// left GetNodeAsync (the single-node page) untouched. GetNodeAsync reused the whole-board GetAsync
// to avoid duplicating the enrichment logic — but that means a single-node page render was paying:
// (a) the SAME full-board body fetch (every sibling's markdown, every temporal version) that
// board-read-loads-all-bodies flags, PLUS (b) BuildNodeIndexAsync (a project-wide, per-board scan)
// built TWICE — once inside GetAsync, once again explicitly in GetNodeAsync for the relation panel.
// These tests pin the fix at the data layer with NUMBERS, the same instrument BoardPageCostTests
// uses (CountingTasksDbFactory: connection opens + SQL statements), not a timing guess.
public sealed class NodePageCostTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly CountingTasksDbFactory _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public NodePageCostTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-nodecost-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new CountingTasksDbFactory(Path.Combine(_dir, "tasks"));
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_relations = new RelationStore(_factory);
		_tasks = new TasksService(_store, _relations, new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// N boards ("simple" kind — no delivery def, so ComputeSpecDeliveryAsync never runs and doesn't
	// dilute the measurement), `nodesPerBoard` nodes each, each body a distinct large marker so a
	// leaked sibling body would be unmistakable if the contract ever changed to expose it.
	async Task<string> SeedProjectAsync(int boardCount, int nodesPerBoard, string prefix = "board")
	{
		string? targetNodeId = null;
		for (var b = 0; b < boardCount; b++)
		{
			var board = $"{prefix}{b:d2}";
			await _tasks.CreateBoardAsync(Proj, board, "simple", null, null);
			var patches = Enumerable.Range(0, nodesPerBoard)
				.Select(i => new NodePatch { Key = $"n{i:d3}", Title = $"N{i}", Body = new string('x', 2000) + $"-b{b}-n{i}" })
				.ToList();
			await _tasks.UpsertAsync(Proj, board, patches);
			if (b == 0)
			{
				var view = await _tasks.GetAsync(Proj, board);
				targetNodeId = view.Nodes.Single(n => n.Key == "n000").NodeId;
			}
		}
		return targetNodeId!;
	}

	// The regression this card exists to prevent: GetNodeAsync's connection/statement count must
	// NOT scale with the number of BOARDS in the project. BuildNodeIndexAsync's scan IS
	// project-wide by necessity (it resolves link targets that may live on any board), but since
	// node-index-scan-one-select-per-board that is ONE query over the whole plan_nodes table
	// (Board is a plain column, not a per-board connection/loop) — the per-board slope should be
	// ~0, not merely "not doubled".
	[Fact]
	public async Task GetNodeAsync_StatementCount_ScalesLinearlyOnce_NotTwice_WithBoardCount()
	{
		var fewNodeId = await SeedProjectAsync(boardCount: 2, nodesPerBoard: 5, prefix: "few");
		_factory.Reset();
		await _tasks.GetNodeAsync(Proj, fewNodeId);
		var fewBoardsStatements = _factory.Statements;

		var manyNodeId = await SeedProjectAsync(boardCount: 10, nodesPerBoard: 5, prefix: "many");
		_factory.Reset();
		await _tasks.GetNodeAsync(Proj, manyNodeId);
		var manyBoardsStatements = _factory.Statements;

		// Measured on this exact fixture, in the three states this card walked through:
		//   original (per-board foreach in BuildNodeIndexAsync, built TWICE)  few=16 many=36 delta=20
		//   one-select-per-board only (still the whole-board builder)         few=12 many=12 delta=0
		//   + node page no longer a board scan (current)                      few=8  many=8  delta=0
		// EXACT 0, not "less than a small number": the node page issues no per-board work at all
		// now, so any reintroduced per-board loop — even one statement per board — must fail here.
		// A `<` threshold would silently tolerate exactly the regression this test exists to catch.
		var delta = manyBoardsStatements - fewBoardsStatements;
		delta.Should().Be(0, "a single-node render must issue no per-board work whatsoever");
	}

	// GetNodeAsync must not re-fetch the project-wide node index a second time: with a single
	// board, the OLD (duplicated) shape opened the index-building connection twice; the fixed
	// shape opens it once. This does not scale with board count (unlike the test above, which
	// isolates the SLOPE) — it pins the flat "called once" invariant directly on a minimal project.
	[Fact]
	public async Task GetNodeAsync_ConnectionCount_StaysSmall_SingleBoardProject()
	{
		var nodeId = await SeedProjectAsync(boardCount: 1, nodesPerBoard: 5);
		_factory.Reset();

		await _tasks.GetNodeAsync(Proj, nodeId);

		// Measured on this single-board fixture: 13 opens originally, 11 after the per-board loops
		// collapsed, 8 now that the node page stopped going through the whole-board builder. Pinned
		// AT the measured number, not comfortably above it — the two-unit slack a `<13` left would
		// have let a regression back to 12 pass unnoticed. This catches a DIFFERENT axis than the
		// slope test above: any change that starts opening a connection per board or per node.
		_factory.Opens.Should().BeLessThanOrEqualTo(8);
	}

	// The OTHER half of "bounded by this node, not by its surroundings": the board-count slope test
	// above pins independence from how many BOARDS exist; this pins independence from how many
	// NODES share the board. That axis is what the original defect actually cost — rendering one
	// card on the 477-node `work` board paid for all 477 (tasks-ui-pages-getting-slower fixed the
	// bodies; node-page-cost-bounded-by-degree stopped fetching the siblings at all).
	// The fixture's nodes carry no relations, so degree and ancestor depth are 0 either way — the
	// only variable is how many siblings the render could have been tempted to read.
	[Fact]
	public async Task GetNodeAsync_StatementCount_DoesNotScale_WithNodesOnTheBoard()
	{
		var smallNodeId = await SeedProjectAsync(boardCount: 1, nodesPerBoard: 5, prefix: "small");
		_factory.Reset();
		await _tasks.GetNodeAsync(Proj, smallNodeId);
		var smallBoardStatements = _factory.Statements;

		var bigNodeId = await SeedProjectAsync(boardCount: 1, nodesPerBoard: 200, prefix: "big");
		_factory.Reset();
		await _tasks.GetNodeAsync(Proj, bigNodeId);
		var bigBoardStatements = _factory.Statements;

		bigBoardStatements.Should().Be(smallBoardStatements,
			"a 200-node board must cost a single-node render exactly what a 5-node board costs");
	}

	// The node page must still render the CORRECT, full body of the requested node — the
	// includeBody:false whole-board fetch + separate single-row patch-back must not lose or
	// truncate it.
	[Fact]
	public async Task GetNodeAsync_StillReturnsFullBody_DespiteBoardWideFetchSkippingBodies()
	{
		var nodeId = await SeedProjectAsync(boardCount: 3, nodesPerBoard: 4);

		var detail = await _tasks.GetNodeAsync(Proj, nodeId);

		detail.Should().NotBeNull();
		detail!.Node.Body.Should().Contain("-b0-n0").And.StartWith(new string('x', 2000));
	}
}
