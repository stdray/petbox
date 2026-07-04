using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// Zero-loss migration from legacy per-board files (tasks/<project>/<board>.db) to the
// per-project file (tasks/<project>.db), with Board stamped, originals kept, idempotent.
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
		TestDirs.CleanupOrDefer(_dir);
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
		TestDirs.ClearPoolsUnder(_tasksDir);
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

	// Regression for the prod cutover: an OLD-schema board file (M001 era — integer Status,
	// no Type/NodeId) must still migrate. The migrator copies it to a temp and runs the full
	// migration chain (M002..M005) there, so the integer Status becomes a slug etc.
	[Fact]
	public void Migrate_UpgradesOldSchemaBoardFile_ThroughEnsure()
	{
		var dir = Path.Combine(_tasksDir, "proj");
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, "legacy.db");
		using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
		{
			c.Open();
			using var cmd = c.CreateCommand();
			cmd.CommandText = """
				CREATE TABLE plan_nodes (Key TEXT NOT NULL, Version INTEGER NOT NULL, Status INTEGER NOT NULL,
					Name TEXT NOT NULL DEFAULT '', Body TEXT NOT NULL, CommitRef TEXT, Priority INTEGER NOT NULL DEFAULT 0,
					PrevKey TEXT, ActiveFrom INTEGER NOT NULL, ActiveTo INTEGER, Created TEXT NOT NULL, Updated TEXT NOT NULL,
					PRIMARY KEY (Key, Version));
				CREATE UNIQUE INDEX ux_plan_nodes_active_key ON plan_nodes (Key) WHERE ActiveTo IS NULL;
				CREATE TABLE VersionInfo (Version INTEGER NOT NULL, AppliedOn DATETIME, Description TEXT);
				INSERT INTO VersionInfo (Version, AppliedOn, Description) VALUES (1,'2026-01-01','M001');
				INSERT INTO plan_nodes (Key,Version,Status,Name,Body,Priority,ActiveFrom,ActiveTo,Created,Updated) VALUES
					('a',1,1,'A','x',0,1,NULL,'2026-01-01','2026-01-01');
				""";
			cmd.ExecuteNonQuery();
		}
		TestDirs.ClearPoolsUnder(_tasksDir);

		new LegacyTaskFileMigrator(_tasksDir, _factory).Migrate().Should().Be(1);

		var pdb = _factory.GetDb("proj");
		var node = pdb.PlanNodes.Single(n => n.Board == "legacy");
		node.Status.Should().Be("InProgress"); // integer 1 remapped via the migration chain
		node.NodeId.Length.Should().Be(32);     // backfilled by M004
		File.Exists(Path.Combine(_tasksDir, "proj", "legacy.db.migrated")).Should().BeTrue();
	}
}
