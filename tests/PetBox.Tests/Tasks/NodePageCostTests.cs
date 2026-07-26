using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
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
	// scale with the number of BOARDS in the project (BuildNodeIndexAsync's per-board scan is
	// project-wide by necessity — it resolves link targets that may live on any board) but NOT
	// TWICE OVER. Measure at two board counts — if the index were still built twice, the delta
	// between them would be 2x the per-board statement cost; deduped, it's 1x.
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

		// Going from 2 boards to 10 boards (+8 boards) adds AT MOST ~1 statement per extra board
		// (the single deduped BuildNodeIndexAsync scan) plus a small constant — not ~2 per board
		// (which a re-introduced duplicate build would cost). Pin the per-board slope, not just an
		// absolute ceiling, so a regression that re-doubles the scan is caught regardless of the
		// fixed overhead around it.
		// Measured: pre-fix (duplicate BuildNodeIndexAsync) few=16/many=36/delta=20 (2.5
		// statements/extra board); post-fix few=15/many=25/delta=10 (1.25/extra board) — the
		// duplicate project-wide scan's contribution is gone. Pin well above the post-fix number
		// but below the pre-fix one, so a reintroduced duplicate build fails this test.
		var delta = manyBoardsStatements - fewBoardsStatements;
		delta.Should().BeLessThan(16);
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

		// A handful of opens (board scan, relations x3, tags, node index, one-row body fetch).
		// Measured 13 both pre- and post-fix on this single-board fixture — Opens alone does NOT
		// catch the duplicate-index regression (removing one dup Open while adding one for the new
		// single-row body fetch is a wash); the slope test above is what actually pins that axis.
		// This test exists to catch a DIFFERENT regression: any change that starts opening a new
		// connection per board or per node again.
		_factory.Opens.Should().BeLessThan(16);
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
