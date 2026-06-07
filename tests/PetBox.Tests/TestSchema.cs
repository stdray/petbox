using Microsoft.Data.Sqlite;
using PetBox.Core.Data;

namespace PetBox.Tests;

// Building the Core (petbox.db) schema with FluentMigrator — a fresh DI container, an
// assembly scan and MigrateUp — costs ~0.2s and runs in EVERY test constructor. Across
// the suite that is ~100s of pure setup (it doesn't even show up in per-test durations,
// so the suite's wall-clock dwarfs the sum of test times). Pay it ONCE: migrate into a
// template file, then copy that file per test. Isolation is unchanged — each test still
// gets its own physical DB — but setup is a file copy instead of a migration run.
public static class TestSchema
{
	static readonly Lazy<string> CoreTemplate = new(BuildCoreTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

	static string BuildCoreTemplate()
	{
		var path = Path.Combine(Path.GetTempPath(), "petbox-tmpl-core-" + Guid.NewGuid().ToString("N") + ".db");
		MigrationRunner.Run($"Data Source={path}");
		// Fold any WAL back into the .db file and release the OS handle, so the copied
		// snapshot is complete and not locked. No-op if the file is in rollback-journal mode.
		using (var conn = new SqliteConnection($"Data Source={path}"))
		{
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
			cmd.ExecuteNonQuery();
		}
		SqliteConnection.ClearAllPools();
		return path;
	}

	// Materialize the Core schema at the DB file named by `connectionString` — a drop-in
	// replacement for MigrationRunner.Run(cs) in test setup that copies the migrated
	// template instead of re-running every migration. Idempotent like the migration run it
	// replaces: if the file already exists (a WebApplicationFactory test that keeps a static
	// DB open and re-invokes setup per test) it's left untouched — overwriting it would yank
	// the file out from under the live host. Fresh per-test dirs always get a copy.
	public static void Core(string connectionString)
	{
		var target = new SqliteConnectionStringBuilder(connectionString).DataSource;
		if (File.Exists(target)) return;
		File.Copy(CoreTemplate.Value, target);
	}
}
