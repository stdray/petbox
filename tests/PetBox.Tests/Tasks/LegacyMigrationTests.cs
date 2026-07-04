using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// Proves the in-place per-board prod migration (M002/M003/M004): an old file with
// the M001-era schema (Status INTEGER enum, no Type/NodeId) is upgraded on Ensure —
// integer statuses become slugs and active nodes get a stable NodeId. This is the
// whole "rollover" story: deploy → each board file migrates itself on first open.
public sealed class LegacyMigrationTests
{
	[Fact]
	public void LegacyIntegerStatus_MigratesToSlug_AndBackfillsNodeId()
	{
		var dir = Path.Combine(Path.GetTempPath(), "petbox-legacy-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var cs = $"Data Source={Path.Combine(dir, "roadmap.db")}";
		try
		{
			// An OLD-schema file at version 1 (M001 era), with integer-enum statuses.
			using (var c = new SqliteConnection(cs))
			{
				c.Open();
				Exec(c, """
					CREATE TABLE plan_nodes (
						Key TEXT NOT NULL, Version INTEGER NOT NULL, Status INTEGER NOT NULL,
						Name TEXT NOT NULL DEFAULT '', Body TEXT NOT NULL, CommitRef TEXT,
						Priority INTEGER NOT NULL DEFAULT 0, PrevKey TEXT,
						ActiveFrom INTEGER NOT NULL, ActiveTo INTEGER,
						Created TEXT NOT NULL, Updated TEXT NOT NULL, PRIMARY KEY (Key, Version));
					CREATE UNIQUE INDEX ux_plan_nodes_active_key ON plan_nodes (Key) WHERE ActiveTo IS NULL;
					CREATE TABLE VersionInfo (Version INTEGER NOT NULL, AppliedOn DATETIME, Description TEXT);
					INSERT INTO VersionInfo (Version, AppliedOn, Description) VALUES (1, '2026-01-01', 'M001');
					INSERT INTO plan_nodes (Key,Version,Status,Name,Body,Priority,ActiveFrom,ActiveTo,Created,Updated) VALUES
						('a',1,1,'A','x',0,1,NULL,'2026-01-01','2026-01-01'),
						('b',1,2,'B','x',0,1,NULL,'2026-01-01','2026-01-01');
					""");
			}
			TestDirs.ClearPoolsUnder(dir);

			// New code opens the file → M002/M003/M004 run in place.
			TasksSchema.Ensure(cs);

			using var db = new TasksDb(TasksDb.CreateOptions(cs));
			var nodes = db.PlanNodes.Where(n => n.ActiveTo == null).OrderBy(n => n.Key).ToList();

			nodes.Should().HaveCount(2);
			nodes[0].Status.Should().Be("InProgress"); // 1 -> slug
			nodes[1].Status.Should().Be("Done");        // 2 -> slug
			nodes.Should().OnlyContain(n => n.NodeId.Length == 32); // stable id backfilled
			nodes[0].NodeId.Should().NotBe(nodes[1].NodeId);
		}
		finally
		{
			TestDirs.CleanupOrDefer(dir);
		}
	}

	static void Exec(SqliteConnection c, string sql)
	{
		using var cmd = c.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}
}
