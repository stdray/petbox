using Microsoft.Data.Sqlite;

namespace PetBox.Core.Data;

// Point-in-time snapshots of every internal SQLite db under the data dir, via
// VACUUM INTO — produces a single consistent file with no -wal/-shm sidecar, safe
// under WAL and concurrent readers. Snapshots land in
// {dataDir}/backups/{stamp}-{label}/ mirroring the source layout, and we keep the
// last `retainSets`. Protects the dogfooding experiment's accumulated data from a
// bad migration or corruption (we can't hand-fix the remote DB).
public static class Backup
{
	public const string BackupsDirName = "backups";

	// PetBox's own log dbs (data/logs/{project}/{log}.db — the same dir the LogDb
	// factory is rooted at in Program.cs). Deliberately NOT snapshotted.
	//
	// WHY: logs are telemetry, not data. Backups restore business state; log/metric
	// history is expendable. Owner decision 2026-07-11 — they were 79% of every set
	// (7.3 GB of offsite backups against 635 MB of live data), so the whole subtree
	// is excluded, knowingly accepting that a restore comes back with no log history.
	//
	// Everything else under dataDir IS data and stays in the set: petbox.db, deploy.db,
	// db/** (user schemas / data_schema_apply), memory/**, tasks/**, sessions/**, config/**.
	public const string ExcludedLogsDirName = "logs";

	// Snapshots every *.db under dataDir (except the backups dir itself and the
	// excluded logs subtree) into a new set folder, then prunes old sets. Returns the
	// relative paths copied.
	public static IReadOnlyList<string> SnapshotAll(string dataDir, string stamp, string label, int retainSets)
	{
		var backupsRoot = Path.Combine(dataDir, BackupsDirName);
		var setDir = Path.Combine(backupsRoot, $"{stamp}-{label}");
		Directory.CreateDirectory(setDir);

		var copied = new List<string>();
		foreach (var src in EnumerateDbs(dataDir, backupsRoot))
		{
			var rel = Path.GetRelativePath(dataDir, src);
			var dest = Path.Combine(setDir, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
			VacuumInto(src, dest);
			copied.Add(rel);
		}

		Prune(backupsRoot, retainSets);
		return copied;
	}

	static IEnumerable<string> EnumerateDbs(string dataDir, string backupsRoot)
	{
		if (!Directory.Exists(dataDir)) return [];
		// Trailing separator so the prefix match can't swallow a sibling like
		// `logs-archive/` — only the `logs/` directory itself is excluded.
		var logsRoot = Path.Combine(dataDir, ExcludedLogsDirName) + Path.DirectorySeparatorChar;
		return Directory.EnumerateFiles(dataDir, "*.db", SearchOption.AllDirectories)
			.Where(p => !p.StartsWith(backupsRoot, StringComparison.OrdinalIgnoreCase)
				&& !p.StartsWith(logsRoot, StringComparison.OrdinalIgnoreCase));
	}

	static void VacuumInto(string srcDbPath, string destDbPath)
	{
		// VACUUM INTO requires the target not to exist.
		if (File.Exists(destDbPath)) File.Delete(destDbPath);

		using var conn = new SqliteConnection($"Data Source={srcDbPath}");
		conn.Open();
		using var cmd = conn.CreateCommand();
		// VACUUM INTO takes a string literal target, not a bound parameter; escape
		// any single quotes in the path (backslashes are literal in SQLite strings).
		cmd.CommandText = $"VACUUM INTO '{destDbPath.Replace("'", "''")}';";
		cmd.ExecuteNonQuery();
	}

	static void Prune(string backupsRoot, int retainSets)
	{
		if (!Directory.Exists(backupsRoot)) return;
		// Set folder names start with a sortable yyyyMMdd-HHmmss stamp; keep newest.
		var stale = Directory.GetDirectories(backupsRoot)
			.OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
			.Skip(Math.Max(0, retainSets))
			.ToList();
		foreach (var old in stale)
		{
			try { Directory.Delete(old, recursive: true); }
			catch (IOException) { /* locked; next pass retries */ }
		}
	}
}
