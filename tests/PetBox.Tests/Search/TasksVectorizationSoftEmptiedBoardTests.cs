using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tests.Memory;
using PetBox.Web.Search;

namespace PetBox.Tests.Search;

// work tasks-vectorization-skips-soft-emptied-boards: TasksVectorizationJob enumerated boards via
// `.Where(n => n.ActiveTo == null).Select(n => n.Board).Distinct()` — a board whose last active
// nodes were just soft-deleted has NO active rows left and dropped out of the enumeration
// entirely, taking its undelivered deletions with it. The vectors of those deleted nodes then sit
// in search_vec forever: orphaned, and nothing left to pick them up, because only that board's own
// drain could have. MemoryVectorizationJob enumerates stores with no such filter — the fix brings
// the tasks job to the same shape.
//
// The other half of the same invariant ("a board drains exactly as long as it owes undelivered
// deltas"): TasksService.DeleteBoardAsync hard-deletes the board's TaskNode rows but used to leave
// its search_cursor row behind. Harmless on its own (board enumeration reads TaskNode, not
// search_cursor, so a physically-gone board's ghost cursor row was never re-visited) — but it is
// leftover state for a board that will never own another delta, so DeleteBoardAsync now purges it
// alongside the FTS/meta/vector purge it already does.
public sealed class TasksVectorizationSoftEmptiedBoardTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ProjectCatalog _catalog;

	public TasksVectorizationSoftEmptiedBoardTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-softempty-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_catalog = new ProjectCatalog(_db.Factory());
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	ScopedDbFactory<TasksDb> TasksFactory() =>
		new(Path.Combine(_dir, "tasks"), Scope.Project, c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);

	TasksService NewTasksService(ScopedDbFactory<TasksDb> factory) =>
		new(new TaskBoardStore(_db.Factory(), factory), new RelationStore(factory),
			new TagStore(factory), new CommentService(factory), llm: null);

	int TasksCount(string sql)
	{
		using var db = new TasksDb(TasksDb.CreateOptions($"Data Source={Path.Combine(_dir, "tasks", Proj + ".db")}"));
		return db.Execute<int>(sql);
	}

	// ---- (1) a soft-emptied board must keep draining its undelivered deletes ----

	[Fact]
	public async Task SoftEmptiedBoard_KeepsDraining_AndItsOrphanVectorsLeaveTheIndex()
	{
		var factory = TasksFactory();
		var tasks = NewTasksService(factory);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		var r = await tasks.UpsertAsync(Proj, "b", new[]
		{
			new NodePatch { Key = "n1", Version = 0, Title = "t1", Body = "some body text one" },
			new NodePatch { Key = "n2", Version = 0, Title = "t2", Body = "some body text two" },
		});
		r.Result.Applied.Should().BeTrue();

		// Pass 1: both nodes get embedded — the baseline the bug would otherwise leave stranded.
		await new TasksVectorizationJob(TasksFactory(), _catalog, new FakeLlmClient()).DrainAllAsync(default);
		TasksCount("SELECT COUNT(*) FROM search_vec").Should().Be(2, "both nodes were indexed");

		// Soft-delete every node on the board — the board now has ZERO rows with ActiveTo == null.
		var view = await tasks.GetAsync(Proj, "b");
		var patches = view.Nodes.Select(n => new NodePatch { Key = n.Key, Version = n.Version, Deleted = true }).ToArray();
		var del = await tasks.UpsertAsync(Proj, "b", patches);
		del.Result.Applied.Should().BeTrue();
		TasksCount("SELECT COUNT(*) FROM plan_nodes WHERE Board = 'b' AND ActiveTo IS NULL").Should().Be(0,
			"the board is now soft-emptied — this is the exact condition the bug drops from enumeration");

		// Pass 2: the drain must still visit board "b" — it owes two undelivered deletes.
		await new TasksVectorizationJob(TasksFactory(), _catalog, new FakeLlmClient()).DrainAllAsync(default);

		TasksCount("SELECT COUNT(*) FROM search_vec").Should().Be(0,
			"RED without the fix: the board dropped out of enumeration (no ActiveTo==null rows left), " +
			"so the drain never visits it again and these two vectors are orphaned forever");
	}

	// ---- (2) DeleteBoardAsync purges the board's own search_cursor row too ----

	[Fact]
	public async Task DeleteBoardAsync_PurgesTheBoardsSearchCursorRow()
	{
		var factory = TasksFactory();
		var tasks = NewTasksService(factory);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		var r = await tasks.UpsertAsync(Proj, "b",
			new[] { new NodePatch { Key = "n1", Version = 0, Title = "t1", Body = "some body text" } });
		r.Result.Applied.Should().BeTrue();

		await new TasksVectorizationJob(TasksFactory(), _catalog, new FakeLlmClient()).DrainAllAsync(default);
		TasksCount("SELECT COUNT(*) FROM search_cursor WHERE IndexName = 'b'").Should().Be(1,
			"the drain left the board's bare-name cursor row behind, as expected");

		(await tasks.DeleteBoardAsync(Proj, "b")).Should().BeTrue();

		TasksCount("SELECT COUNT(*) FROM search_cursor WHERE IndexName = 'b'").Should().Be(0,
			"RED without the fix: DeleteBoardAsync purges FTS/meta/vector docs but leaves the cursor " +
			"row behind — dead state for a board that can never own another delta");
	}
}
