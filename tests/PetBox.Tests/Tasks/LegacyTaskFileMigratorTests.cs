using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// Zero-loss migration from legacy per-board files (tasks/<project>/<board>.db) to the
// per-project file (tasks/<project>.db), with Board stamped, originals kept, idempotent.
[Collection("DataModule")]
public sealed class LegacyTaskFileMigratorTests : IDisposable
{
	readonly string _dir;
	readonly string _tasksDir;
	readonly ScopedDbFactory<TasksDb> _factory;

	public LegacyTaskFileMigratorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-legacymig-" + Guid.NewGuid().ToString("N"));
		_tasksDir = Path.Combine(_dir, "tasks");
		Directory.CreateDirectory(_tasksDir);
		_factory = new ScopedDbFactory<TasksDb>(_tasksDir, Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	// A legacy per-board file with `count` active nodes (keys phase-1..phase-N), Board unset.
	void SeedLegacyBoard(string project, string board, int count)
	{
		var dir = Path.Combine(_tasksDir, project);
		Directory.CreateDirectory(dir);
		var cs = $"Data Source={Path.Combine(dir, board + ".db")};Pooling=False";
		TasksSchema.Ensure(cs);
		using var db = new TasksDb(TasksDb.CreateOptions(cs));
		var now = DateTime.UtcNow;
		for (var i = 1; i <= count; i++)
			db.Insert(new PlanNode { Key = $"phase-{i}", Version = i, Status = "Pending", Name = $"N{i}", Body = "b", Priority = i, NodeId = Guid.NewGuid().ToString("N"), ActiveFrom = i, Created = now, Updated = now });
		SqliteConnection.ClearAllPools();
	}

	[Fact]
	public void Migrate_MovesEachBoardIntoProjectFile_StampingBoard_KeepingOriginals()
	{
		SeedLegacyBoard("proj", "roadmap", 2);
		SeedLegacyBoard("proj", "spec", 3);

		var migrated = new LegacyTaskFileMigrator(_tasksDir, _factory).Migrate();
		migrated.Should().Be(2);

		var pdb = _factory.GetDb("proj");
		pdb.PlanNodes.Count(n => n.Board == "roadmap").Should().Be(2);
		pdb.PlanNodes.Count(n => n.Board == "spec").Should().Be(3);
		// The same key in two boards coexists (distinct PK Board,Key,Version).
		pdb.PlanNodes.Count(n => n.Key == "phase-1").Should().Be(2);

		// Originals kept (renamed), not deleted.
		File.Exists(Path.Combine(_tasksDir, "proj", "roadmap.db")).Should().BeFalse();
		File.Exists(Path.Combine(_tasksDir, "proj", "roadmap.db.migrated")).Should().BeTrue();

		// Idempotent: a second run migrates nothing.
		new LegacyTaskFileMigrator(_tasksDir, _factory).Migrate().Should().Be(0);
	}
}
