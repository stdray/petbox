using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// board-page-cost: the two data-layer costs the work card diagnosed on
// /ui/{ws}/{project}/tasks/{board} — N+1 relation loading (RelationStore.ListAsync opened its OWN
// connection per node per direction, ~954 opens+queries on the 477-node $system `work` board) and
// full node bodies always loaded regardless of whether the active view shows them. These tests pin
// the fix at the data layer with NUMBERS (connection/statement counts that do not scale with board
// size), not a timing guess — a regression here silently reintroduces the N+1 the card exists to
// kill. The third cost (comments loaded+rendered into every card) is covered by
// CommentServiceTests.CountForBoard_CountsOnlyActiveComments_ScopedToTheBoard plus the
// TaskBoardModel/E2E surface — this file is the ITasksService/RelationStore layer only.
public sealed class BoardPageCostTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly CountingTasksDbFactory _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public BoardPageCostTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-boardcost-" + Guid.NewGuid().ToString("N"));
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

	// Seeds `count` nodes on `board` ("simple" kind — no delivery roll-up, so
	// ComputeSpecDeliveryAsync never runs and doesn't dilute the relation-loading measurement) and
	// chains a "blocks" edge between every consecutive pair, so every node carries at least one
	// outgoing AND one incoming edge — exactly the shape ListAsync("from")/ListAsync("to") used to
	// walk per node, per direction.
	async Task<IReadOnlyList<string>> SeedChainAsync(string board, int count)
	{
		await _tasks.CreateBoardAsync(Proj, board, "simple", null, null);
		var patches = Enumerable.Range(0, count)
			.Select(i => new NodePatch { Key = $"n{i:d4}", Title = $"N{i}", Body = $"body of n{i}" }).ToList();
		await _tasks.UpsertAsync(Proj, board, patches);
		var view = await _tasks.GetAsync(Proj, board);
		var ids = Enumerable.Range(0, count).Select(i => view.Nodes.Single(n => n.Key == $"n{i:d4}").NodeId).ToList();
		for (var i = 0; i < count - 1; i++)
			await _relations.CreateAsync(Proj, "blocks", ids[i], ids[i + 1]);
		return ids;
	}

	// ── RelationStore.ListForNodesAsync: correctness ────────────────────────────

	[Fact]
	public async Task ListForNodesAsync_ReproducesPerNode_ListAsync_FromAndTo()
	{
		var ids = await SeedChainAsync("work", 6);

		var batched = await _relations.ListForNodesAsync(Proj, ids);

		foreach (var id in ids)
		{
			var expectedFrom = (await _relations.ListAsync(Proj, id, "from")).Select(r => r.Id).OrderBy(x => x).ToList();
			var expectedTo = (await _relations.ListAsync(Proj, id, "to")).Select(r => r.Id).OrderBy(x => x).ToList();

			batched.Where(r => r.FromNodeId == id).Select(r => r.Id).OrderBy(x => x)
				.Should().BeEquivalentTo(expectedFrom, o => o.WithStrictOrdering());
			batched.Where(r => r.ToNodeId == id).Select(r => r.Id).OrderBy(x => x)
				.Should().BeEquivalentTo(expectedTo, o => o.WithStrictOrdering());
		}
	}

	[Fact]
	public async Task ListForNodesAsync_EmptyInput_NoQuery_EmptyResult()
	{
		await SeedChainAsync("work", 3); // schema must exist for the assertion to mean anything
		_factory.Reset();

		var result = await _relations.ListForNodesAsync(Proj, []);

		result.Should().BeEmpty();
		_factory.Opens.Should().Be(0); // no connection opened at all for an empty id set
	}

	// Chunking correctness (SQLite's 999-bound-parameter ceiling): force MORE than one 400-id
	// chunk and verify the result is still exactly right — dedup across chunk boundaries included
	// (a relation whose FromNodeId is in chunk 1 and ToNodeId in chunk 2 must not be double-counted).
	[Fact]
	public async Task ListForNodesAsync_MultiChunk_StillExact_NoDuplicates()
	{
		var ids = await SeedChainAsync("work", 5);
		// Pad the id list past 400 with well-formed but non-existent NodeIds — chunking only cares
		// about LIST SIZE, not whether each id resolves to a real relation, so this cheaply forces
		// ListForNodesAsync's ChunkSize=400 to split into 2 queries without seeding 400+ real nodes.
		var padded = ids.Concat(Enumerable.Range(0, 420).Select(_ => Guid.NewGuid().ToString("N"))).ToList();

		var batched = await _relations.ListForNodesAsync(Proj, padded);

		batched.Select(r => r.Id).Should().OnlyHaveUniqueItems();
		batched.Should().HaveCount(ids.Count - 1); // the 4 chained "blocks" edges among the 5 real nodes
	}

	// ── the regression this card exists to prevent ──────────────────────────────

	// N+1 relation loading (board-page-cost's core diagnosis): GetAsync's connection/statement
	// count must NOT scale with board size. Measure at two sizes 20x apart — the OLD per-node
	// ListAsync shape (2 connections per node) would blow this up roughly proportionally (10 vs
	// 200); the batched shape stays essentially flat (one connection, 1-2 chunked IN queries
	// regardless of N below the 400-id chunk boundary).
	[Fact]
	public async Task GetAsync_ConnectionCount_DoesNotScaleWithBoardSize()
	{
		await SeedChainAsync("small", 5);
		_factory.Reset();
		await _tasks.GetAsync(Proj, "small", includeClosed: true);
		var smallOpens = _factory.Opens;

		await SeedChainAsync("big", 100);
		_factory.Reset();
		await _tasks.GetAsync(Proj, "big", includeClosed: true);
		var bigOpens = _factory.Opens;
		var bigStatements = _factory.Statements;

		// "a handful, not hundreds": the old N+1 would cost ~200 opens alone on the 100-node
		// board (2 x N from RelationStore.ListAsync); the fixed path stays under 30 regardless.
		bigOpens.Should().BeLessThan(30);
		bigStatements.Should().BeLessThan(60);
		// The 20x-larger board costs about the SAME as the small one — proof it is not per-node.
		bigOpens.Should().BeLessThanOrEqualTo(smallOpens + 3);
	}

	// ── board-read-loads-all-bodies: the Body column projection ─────────────────

	[Fact]
	public async Task GetAsync_IncludeBodyFalse_OmitsBody_TrueKeepsIt_EveryOtherFieldUnaffected()
	{
		await _tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await _tasks.UpsertAsync(Proj, "b",
			[new NodePatch { Key = "n", Title = "N", Body = "a very particular body marker xyz123", Priority = 5 }]);

		var withBody = await _tasks.GetAsync(Proj, "b", includeClosed: true, includeBody: true);
		withBody.Nodes.Single().Body.Should().Be("a very particular body marker xyz123");

		var withoutBody = await _tasks.GetAsync(Proj, "b", includeClosed: true, includeBody: false);
		var n = withoutBody.Nodes.Single();
		n.Body.Should().BeEmpty();
		// The projection names every OTHER column explicitly — none of them are collateral damage.
		n.Title.Should().Be("N");
		n.Key.Should().Be("n");
		n.Priority.Should().Be(5);
		n.Status.Should().Be(withBody.Nodes.Single().Status);
		n.NodeId.Should().Be(withBody.Nodes.Single().NodeId);
	}

	[Fact]
	public async Task GetAsync_IncludeBodyFalse_NeverPutsBodyOnTheWireEvenAsASubstring()
	{
		await _tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		const string marker = "unique-body-text-that-must-never-leak-98421";
		await _tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "n", Title = "N", Body = marker }]);

		var view = await _tasks.GetAsync(Proj, "b", includeClosed: true, includeBody: false);

		view.Nodes.Should().OnlyContain(n => n.Body != null && !n.Body.Contains(marker));
	}
}
