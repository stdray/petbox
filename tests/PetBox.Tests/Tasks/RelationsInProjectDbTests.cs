using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// relations-in-project-db: typed edges live in the PROJECT's tasks file (tasks/{proj}.db,
// table `relations`), not in the Core DB — so their endpoints can carry a REAL foreign key
// to the nodes. plan_nodes is temporal (many revisions per NodeId) and cannot be a FK
// parent, so the FK points at plan_node_ids, a node-identity registry that triggers derive
// from plan_nodes. These tests pin what the DATABASE enforces (not just the service layer),
// and the Core->project backfill.
public sealed class RelationsInProjectDbTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public RelationsInProjectDbTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-relmove-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
		_relations = new RelationStore(_factory);
		_tasks = new TasksService(_store, _relations, new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static NodePatch Node(string key, string? title = null) => new() { Key = key, Title = title ?? key, Body = "b" };

	// Creates a board with two nodes and returns their NodeIds.
	async Task<(string A, string B)> TwoNodesAsync(string board = "work")
	{
		await _tasks.CreateBoardAsync(Proj, board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, board, [Node("alpha"), Node("beta")]);
		var view = await _tasks.GetAsync(Proj, board);
		var a = view.Nodes.Single(n => n.Key == "alpha").NodeId;
		var b = view.Nodes.Single(n => n.Key == "beta").NodeId;
		return (a, b);
	}

	// ── what the DB itself enforces ────────────────────────────────────────────

	// The claim "a real FK" has to be true of the FILE, not just of the C# — FluentMigrator
	// must actually have emitted the inline REFERENCES with ON DELETE CASCADE, and SQLite must
	// have foreign_keys=ON on the connection (TasksDb appends Foreign Keys=True).
	[Fact]
	public async Task Relations_endpoints_carry_a_real_fk_to_the_node_registry_with_cascade()
	{
		await TwoNodesAsync();
		using var db = _factory.GetDb(Proj);

		var fks = db.Query<(string Table, string From, string To, string OnDelete)>(
			r => (r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(6)),
			"PRAGMA foreign_key_list('relations');").ToList();

		fks.Should().HaveCount(2);
		fks.Should().OnlyContain(f => f.Table == "plan_node_ids" && f.To == "NodeId" && f.OnDelete == "CASCADE");
		fks.Select(f => f.From).Should().BeEquivalentTo(["FromNodeId", "ToNodeId"]);

		// ...and the pragma is actually ON for the connections the app uses.
		db.Execute<long>("PRAGMA foreign_keys;").Should().Be(1);
	}

	// The registry is derived from plan_nodes by trigger, never written by app code.
	[Fact]
	public async Task Node_identity_registry_tracks_plan_nodes()
	{
		var (a, b) = await TwoNodesAsync();
		using var db = _factory.GetDb(Proj);
		db.GetTable<PlanNodeId>().Select(n => n.NodeId).ToList().Should().BeEquivalentTo([a, b]);
	}

	// The hole this closes: NodeRefResolver passes ANY 32-hex value through as a NodeId
	// without checking that a node by that id exists, so a dangling edge could be created
	// silently and later render as "missing". Now the store refuses it with a readable error.
	[Fact]
	public async Task Dangling_edge_cannot_be_created_through_the_store()
	{
		var (a, _) = await TwoNodesAsync();
		var ghost = Guid.NewGuid().ToString("N"); // well-formed 32-hex NodeId of no node

		var toGhost = () => _relations.CreateAsync(Proj, "blocks", a, ghost);
		(await toGhost.Should().ThrowAsync<ArgumentException>())
			.WithMessage($"*toNodeId '{ghost}' does not exist in project '{Proj}'*");

		var fromGhost = () => _relations.CreateAsync(Proj, "blocks", ghost, a);
		(await fromGhost.Should().ThrowAsync<ArgumentException>())
			.WithMessage($"*fromNodeId '{ghost}' does not exist*");

		using var db = _factory.GetDb(Proj);
		db.GetTable<Relation>().Count().Should().Be(0);
	}

	// ...and even if a caller bypasses the store's guard entirely, the DATABASE rejects the
	// row. This is the structural claim: a dangling edge is not merely discouraged, it is
	// unrepresentable.
	[Fact]
	public async Task Dangling_edge_is_rejected_by_the_database_even_bypassing_the_store()
	{
		var (a, _) = await TwoNodesAsync();
		using var db = _factory.GetDb(Proj);

		var insert = () => db.Insert(new Relation
		{
			Id = Guid.NewGuid().ToString("N"),
			Kind = "blocks",
			FromNodeId = a,
			ToNodeId = Guid.NewGuid().ToString("N"), // no such node
			CreatedAt = DateTime.UtcNow,
		});

		insert.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(19); // SQLITE_CONSTRAINT
		db.GetTable<Relation>().Count().Should().Be(0);
	}

	// A board delete HARD-deletes its plan_nodes rows — which is exactly how the pre-move
	// edges went dangling (nodes gone from the tasks file, edges left behind in petbox.db).
	// Now the registry row goes with the last revision and the FK cascades the edges away.
	[Fact]
	public async Task Deleting_a_board_cascades_its_edges_away_instead_of_leaving_them_dangling()
	{
		var (a, b) = await TwoNodesAsync();
		await _relations.CreateAsync(Proj, "blocks", a, b);
		(await _relations.ListAsync(Proj, a)).Should().ContainSingle();

		await _tasks.DeleteBoardAsync(Proj, "work");

		using var db = _factory.GetDb(Proj);
		db.GetTable<PlanNodeId>().Count().Should().Be(0);
		db.GetTable<Relation>().Count().Should().Be(0); // cascaded, not dangling
	}

	// A single-node delete is a SOFT close (revisions kept) — the node identity survives, so
	// its edge history stays readable. Closing the active edges is the service's job.
	[Fact]
	public async Task Soft_deleting_a_node_keeps_its_identity_and_edge_history()
	{
		var (a, b) = await TwoNodesAsync();
		await _relations.CreateAsync(Proj, "relates_to", a, b);
		await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = "beta", Deleted = true }]);

		using var db = _factory.GetDb(Proj);
		db.GetTable<PlanNodeId>().Select(n => n.NodeId).ToList().Should().Contain(b);
		(await _relations.ListAsync(Proj, a, includeHistory: true)).Should().ContainSingle();
	}

	// ── the Core -> project backfill ───────────────────────────────────────────

	// Edges survive the move: a legacy Core-DB row lands in the project file under its
	// ORIGINAL id (edge ids stay stable for callers) and reads back through the store.
	[Fact]
	public async Task Backfill_moves_core_edges_into_the_project_file()
	{
		var (a, b) = await TwoNodesAsync();
		var live = SeedLegacy(Proj, "blocks", a, b);
		var closed = SeedLegacy(Proj, "relates_to", a, b, closedAt: DateTime.UtcNow);

		var result = Migrator().Migrate();

		result.Copied.Should().Be(2);
		result.DroppedActive.Should().Be(0);
		var active = await _relations.ListAsync(Proj, a);
		active.Should().ContainSingle();
		active[0].Id.Should().Be(live);           // original id preserved
		active[0].Kind.Should().Be("blocks");
		(await _relations.ListAsync(Proj, a, includeHistory: true)).Select(r => r.Id)
			.Should().BeEquivalentTo([live, closed]); // history came across too
	}

	// Dangling edges are DROPPED (owner's decision) — they cannot be inserted at all (FK) and
	// were only ever renderable as "missing". The loss must be COUNTED, not silent.
	[Fact]
	public async Task Backfill_drops_dangling_edges_and_counts_them()
	{
		var (a, _) = await TwoNodesAsync();
		var ghost = Guid.NewGuid().ToString("N");
		SeedLegacy(Proj, "blocks", a, ghost);              // active + dangling -> dropped, logged
		SeedLegacy(Proj, "part_of", ghost, a);             // active + dangling -> dropped, logged
		SeedLegacy(Proj, "supersedes", a, ghost, closedAt: DateTime.UtcNow); // closed + dangling -> dropped
		var good = SeedLegacy(Proj, "relates_to", a, a);

		var result = Migrator().Migrate();

		result.Copied.Should().Be(1);
		result.DroppedActive.Should().Be(2);
		result.DroppedClosed.Should().Be(1);
		(await _relations.ListAsync(Proj, a, includeHistory: true)).Select(r => r.Id).Should().Equal(good);
	}

	// A project with no tasks file has no nodes at all, so every edge it holds is dangling by
	// definition ($workspace / $ws-* memory pseudo-projects on prod). Report, drop, and do not
	// create an empty file for it.
	[Fact]
	public void Backfill_drops_edges_of_a_project_that_has_no_tasks_file()
	{
		SeedLegacy("$workspace", "supersedes", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));

		var result = Migrator().Migrate();

		result.DroppedActive.Should().Be(1);
		result.Copied.Should().Be(0);
		File.Exists(Path.Combine(_dir, "tasks", "$workspace.db")).Should().BeFalse();
	}

	// Idempotent/resumable: the source rows are NOT removed (the Core table is dropped in a
	// later release), so a re-run must skip what it already copied rather than duplicate it —
	// which is also what makes an interrupted run safe to finish.
	[Fact]
	public async Task Backfill_is_idempotent_and_resumable()
	{
		var (a, b) = await TwoNodesAsync();
		SeedLegacy(Proj, "blocks", a, b);

		Migrator().Migrate().Copied.Should().Be(1);
		var second = Migrator().Migrate();

		second.Copied.Should().Be(0);
		second.Skipped.Should().Be(1);
		(await _relations.ListAsync(Proj, a)).Should().ContainSingle();
	}

	RelationsToTasksDbMigrator Migrator() =>
		new(_db, _factory, Path.Combine(_dir, "tasks"));

	// A row in the LEGACY Core-DB table (the migration source). Returns its id.
	string SeedLegacy(string project, string kind, string from, string to, DateTime? closedAt = null)
	{
		var id = Guid.NewGuid().ToString("N");
		_db.Insert(new LegacyRelation
		{
			Id = id,
			ProjectKey = project,
			Kind = kind,
			FromNodeId = from,
			ToNodeId = to,
			CreatedAt = DateTime.UtcNow,
			ClosedAt = closedAt,
		});
		return id;
	}
}
