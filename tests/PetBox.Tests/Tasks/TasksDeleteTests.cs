using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// The `deleted:true` patch in tasks_upsert: a temporal-close of the active node (history
// kept) that also closes everything hanging off it — edges (both directions), tags, the
// FTS row — and rides the normal upsert result (removed[] + closed). Guards: a node with
// active part_of children is refused via a Rejected conflict; delete cannot combine with
// rename or an upsert of the same key in one batch.
public sealed class TasksDeleteTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public TasksDeleteTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-tasksdel-" + Guid.NewGuid().ToString("N"));
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

	static NodePatch Node(string key, string? partOf = null, string? title = null, string? blockedBy = null, string? status = null, IReadOnlyList<string>? tags = null, long version = 0) => new()
	{
		Key = key,
		PartOf = partOf,
		Title = title ?? key,
		Body = "body of " + key,
		BlockedBy = blockedBy,
		Status = status,
		Tags = tags,
		Version = version,
	};

	static NodePatch Delete(string key, long version = 0) => new() { Key = key, Deleted = true, Version = version };

	// Query-mode read through the unified verb (these fixtures run without an embedder,
	// so the search is lexical-only by construction).
	Task<TaskSearchResult> Search(string q) =>
		_tasks.SearchNodesAsync(Proj, new PetBox.Core.Contract.SearchRequest<TaskNodeFilter, TaskSortBy> { Query = q });

	async Task<(string NodeId, long Version)> NodeInfo(string board, string key)
	{
		var view = await _tasks.GetAsync(Proj, board);
		var n = view.Nodes.Single(n => n.Key == key);
		return (n.NodeId, n.Version);
	}

	[Fact]
	public async Task Delete_Leaf_ClosesRow_Edges_Tags_AndSearch()
	{
		await _tasks.UpsertAsync(Proj, "b", new[]
		{
			Node("parent"),
			Node("leafy", partOf: "parent", title: "Unmistakable leafword", tags: ["area:tasks"]),
		});
		var (leafId, leafVer) = await NodeInfo("b", "leafy");
		(await Search("leafword")).Hits.Should().NotBeEmpty();

		var r = await _tasks.UpsertAsync(Proj, "b", new[] { Delete("leafy", leafVer) });

		r.Result.Applied.Should().BeTrue();
		r.Result.Closed.Should().Be(1);
		r.Result.Removed.Should().Contain("leafy");

		var ctx = _store.GetContext(Proj);
		ctx.PlanNodes.Any(n => n.Key == "leafy" && n.ActiveTo == null).Should().BeFalse(); // temporal-closed
		ctx.PlanNodes.Any(n => n.Key == "leafy" && n.ActiveTo != null).Should().BeTrue();  // history kept
		(await _relations.ListAsync(Proj, leafId, "both")).Should().BeEmpty();             // part_of closed
		ctx.NodeTags.Any(t => t.NodeId == leafId && t.ValidTo == null).Should().BeFalse(); // tags closed
		(await Search("leafword")).Hits.Should().BeEmpty(); // FTS row gone
	}

	[Fact]
	public async Task Delete_ParentWithActiveChild_IsRejectedConflict_NothingWritten()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("parent"), Node("child", partOf: "parent") });
		var (_, parentVer) = await NodeInfo("b", "parent");

		var r = await _tasks.UpsertAsync(Proj, "b", new[] { Delete("parent", parentVer) });

		r.Result.Applied.Should().BeFalse();
		var c = r.Result.Conflicts.Should().ContainSingle().Subject;
		c.Kind.Should().Be(TemporalConflictKind.Rejected);
		c.Key.Should().Be("parent");
		c.Reason.Should().Contain("children");
		_store.GetContext(Proj).PlanNodes.Any(n => n.Key == "parent" && n.ActiveTo == null).Should().BeTrue();
	}

	[Fact]
	public async Task Delete_WholeSubtree_InOneBatch_Applies()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("parent"), Node("child", partOf: "parent") });

		var r = await _tasks.UpsertAsync(Proj, "b", new[] { Delete("parent"), Delete("child") });

		r.Result.Applied.Should().BeTrue();
		r.Result.Removed.Should().BeEquivalentTo("parent", "child");
		_store.GetContext(Proj).PlanNodes.Any(n => n.ActiveTo == null && n.Board == "b").Should().BeFalse();
	}

	[Fact]
	public async Task Delete_StaleBaseline_Conflicts_VersionZero_Unconditional()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("n") });                              // v1
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("n", title: "N-mid", version: 1) });  // -> v2 (concurrent edit)

		// A genuine stale baseline: delete quoting v1 after the node moved to v2. (Was
		// version:999 under exact-match — that is now a FutureBaseline, not Stale.)
		var stale = await _tasks.UpsertAsync(Proj, "b", new[] { Delete("n", version: 1) });
		stale.Result.Applied.Should().BeFalse();
		stale.Result.Conflicts.Should().ContainSingle().Which.Kind.Should().Be(TemporalConflictKind.Stale);

		var force = await _tasks.UpsertAsync(Proj, "b", new[] { Delete("n") }); // version 0
		force.Result.Applied.Should().BeTrue();
		force.Result.Removed.Should().Contain("n");
	}

	[Fact]
	public async Task Delete_MissingKey_IsIdempotentNoOp()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("n") });

		var r = await _tasks.UpsertAsync(Proj, "b", new[] { Delete("ghost") });

		r.Result.Applied.Should().BeTrue();
		r.Result.Closed.Should().Be(0);
		r.Result.Removed.Should().BeEmpty();
	}

	[Fact]
	public async Task Delete_CombinedWith_UpsertOfSameKey_OrRename_Throws()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("n") });

		var both = async () => await _tasks.UpsertAsync(Proj, "b", new[] { Delete("n"), Node("n", version: 1) });
		await both.Should().ThrowAsync<ArgumentException>().WithMessage("*both deleted and upserted*");

		var renamed = async () => await _tasks.UpsertAsync(Proj, "b",
			new[] { new NodePatch { Key = "n2", PrevKey = "n", Deleted = true } });
		await renamed.Should().ThrowAsync<ArgumentException>().WithMessage("*renamed and deleted*");
	}

	[Fact]
	public async Task Delete_Blocker_ClosesEdge_AndUnblocksTarget()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("blocker") });
		var (blockerId, _) = await NodeInfo("b", "blocker");
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("stuck", blockedBy: blockerId, status: "Blocked") });
		var (stuckId, _) = await NodeInfo("b", "stuck");

		var r = await _tasks.UpsertAsync(Proj, "b", new[] { Delete("blocker") });

		r.Result.Applied.Should().BeTrue();
		(await _relations.ListAsync(Proj, stuckId, "to")).Where(e => e.Kind == "blocks").Should().BeEmpty();
		var stuck = _store.GetContext(Proj).PlanNodes.Single(n => n.NodeId == stuckId && n.ActiveTo == null);
		stuck.Status.Should().Be("InProgress"); // Blocked → InProgress, mirroring the Done effect
	}

	[Fact]
	public async Task Delta_AfterDelete_ReportsRemovedKey()
	{
		var up = await _tasks.UpsertAsync(Proj, "b", new[] { Node("n") });
		var cursor = up.Result.CurrentVersion;

		await _tasks.UpsertAsync(Proj, "b", new[] { Delete("n") });

		var delta = await _tasks.DeltaAsync(Proj, "b", cursor);
		delta.Result.Removed.Should().Contain("n");
	}
}
