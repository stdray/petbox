using Microsoft.Data.Sqlite;
using PetBox.Core.Data;

namespace PetBox.Tests.Data;

// A2: VACUUM INTO snapshots produce valid, queryable copies mirroring the source
// layout, and old sets are pruned to retainSets.
public sealed class BackupTests : IDisposable
{
	readonly string _dir;

	public BackupTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-backup-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	[Fact]
	public void SnapshotAll_CopiesValidDb_MirroringLayout()
	{
		// A nested db like the tier layout: {dataDir}/tasks/proj/board.db
		var srcRel = Path.Combine("tasks", "proj", "board.db");
		MakeDb(Path.Combine(_dir, srcRel), rows: 3);

		var copied = Backup.SnapshotAll(_dir, "20260531-120000", "test", retainSets: 14);
		copied.Should().ContainSingle().Which.Replace('\\', '/').Should().Be("tasks/proj/board.db");

		var dest = Path.Combine(_dir, Backup.BackupsDirName, "20260531-120000-test", srcRel);
		File.Exists(dest).Should().BeTrue();
		RowCount(dest).Should().Be(3); // snapshot is a real, queryable db
	}

	[Fact]
	public void SnapshotAll_PrunesToRetainSets()
	{
		MakeDb(Path.Combine(_dir, "core.db"), rows: 1);

		Backup.SnapshotAll(_dir, "20260531-100000", "a", retainSets: 2);
		Backup.SnapshotAll(_dir, "20260531-110000", "b", retainSets: 2);
		Backup.SnapshotAll(_dir, "20260531-120000", "c", retainSets: 2);

		var sets = Directory.GetDirectories(Path.Combine(_dir, Backup.BackupsDirName))
			.Select(Path.GetFileName).OrderBy(n => n).ToList();
		sets.Should().BeEquivalentTo(["20260531-110000-b", "20260531-120000-c"]); // oldest pruned
	}

	// Logs are telemetry, not data (owner decision 2026-07-11): data/logs/** must never
	// land in a set, while every other subtree must. Guards both halves — the exclusion
	// must not take real data with it.
	[Fact]
	public void SnapshotAll_ExcludesLogsButKeepsData()
	{
		MakeDb(Path.Combine(_dir, "logs", "petbox", "app.db"), rows: 1);        // telemetry — excluded
		MakeDb(Path.Combine(_dir, "petbox.db"), rows: 1);                        // data
		MakeDb(Path.Combine(_dir, "deploy.db"), rows: 1);                        // data
		MakeDb(Path.Combine(_dir, "db", "proj", "user.db"), rows: 1);            // data (user schema)
		MakeDb(Path.Combine(_dir, "memory", "proj.db"), rows: 1);                // data
		MakeDb(Path.Combine(_dir, "tasks", "proj", "board.db"), rows: 1);        // data
		MakeDb(Path.Combine(_dir, "sessions", "proj.db"), rows: 1);              // data
		MakeDb(Path.Combine(_dir, "config", "ws.db"), rows: 1);                  // data

		var copied = Backup.SnapshotAll(_dir, "20260711-120000", "test", retainSets: 14)
			.Select(p => p.Replace('\\', '/')).ToList();

		copied.Should().BeEquivalentTo([
			"petbox.db", "deploy.db", "db/proj/user.db", "memory/proj.db",
			"tasks/proj/board.db", "sessions/proj.db", "config/ws.db",
		]);

		var setDir = Path.Combine(_dir, Backup.BackupsDirName, "20260711-120000-test");
		Directory.Exists(Path.Combine(setDir, Backup.ExcludedLogsDirName)).Should().BeFalse();
		File.Exists(Path.Combine(setDir, "db", "proj", "user.db")).Should().BeTrue();
	}

	static void MakeDb(string path, int rows)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		using var conn = new SqliteConnection($"Data Source={path}");
		conn.Open();
		using var create = conn.CreateCommand();
		create.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY);";
		create.ExecuteNonQuery();
		for (var i = 1; i <= rows; i++)
		{
			using var ins = conn.CreateCommand();
			ins.CommandText = $"INSERT INTO t (id) VALUES ({i});";
			ins.ExecuteNonQuery();
		}
	}

	static long RowCount(string path)
	{
		using var conn = new SqliteConnection($"Data Source={path}");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT count(*) FROM t;";
		return Convert.ToInt64(cmd.ExecuteScalar());
	}
}
