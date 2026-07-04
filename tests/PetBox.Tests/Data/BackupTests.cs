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
