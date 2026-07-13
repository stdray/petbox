using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;

namespace PetBox.Tests;

// Bridges test setup — which keeps its own PetBoxDb to seed rows and assert against — to the
// services under test, which now take ICoreDbFactory and open their OWN connection per call
// (core-db-behind-factory). The factory points at the SAME core.db file and carries the SAME
// DataOptions, so it reuses the SHARED MappingSchema (never build a per-connection one — that was
// the ~290 MB prod OOM; see PetBoxDb.SharedMappingSchema).
//
// The test's `_db` and the service's connections are DIFFERENT connections to one file, which is
// exactly the production shape. Every core db in the suite is file-backed, so that resolves to the
// same database; a `Data Source=:memory:` core db would NOT work here (a second connection would
// see an empty database) and none of these tests use one.
public static class TestCoreDb
{
	public static ICoreDbFactory Factory(this PetBoxDb db) =>
		new CoreDbFactory(new DataOptions<PetBoxDb>(db.Options));

	public static ICoreDbFactory CoreFactory(string connectionString) =>
		new CoreDbFactory(connectionString);

	// The MCP tools no longer take a core-db factory — they take the SERVICE that owns core.db for
	// their concern (db-access-layer-cleanup: the database is visible only in the service layer).
	// A unit test that drives a tool directly builds the real service over its own factory: these
	// are the production implementations, not stubs, so the tools are exercised through exactly the
	// door DI hands them at runtime.
	public static IWorkspaceMemoryDirectory WorkspaceMemory(this ICoreDbFactory dbf) =>
		new WorkspaceMemoryDirectory(dbf);

	public static PetBox.Core.Health.IHealthReportService HealthReports(this ICoreDbFactory dbf) =>
		new PetBox.Core.Health.HealthReportService(dbf);

	// The pull-endpoint list — a different table from HealthReports above, and its own door.
	public static PetBox.Core.Health.IHealthEndpointDirectory HealthEndpoints(this ICoreDbFactory dbf) =>
		new PetBox.Core.Health.HealthEndpointDirectory(dbf);

	// ApiKeys' one door. The config-key lookup is EMPTY here (no Auth:ApiKeys section in a unit
	// test) — a config-declared key is a host-level concern and has its own integration coverage.
	public static PetBox.Web.Auth.AgentKeyAdminService AgentKeys(this ICoreDbFactory dbf) =>
		new(dbf,
			new PetBox.Core.Auth.KeyStatService(),
			new PetBox.Core.Auth.ConfigApiKeyLookup(
				Microsoft.Extensions.Options.Options.Create(new PetBox.Core.Auth.ConfigApiKeyOptions())));
}

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
		// Release only this template's pooled handle — a global ClearAllPools here would
		// yank pooled connections out from under tests already running in parallel.
		SqliteConnection.ClearPool(new SqliteConnection($"Data Source={path}"));
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

	// A `Data Source=...;Cache=Shared` connection string for a WebApplicationFactory
	// test's Core db, rooted in a FRESH per-call directory — not a bare filename dropped
	// directly in Path.GetTempPath(). Program.cs derives every scoped module's storage
	// root (logs/config/tasks/memory/db) from Path.GetDirectoryName(this connection
	// string's DataSource); a unique FILENAME with a shared bare-temp-root DIRECTORY still
	// collapses onto ONE physical folder across every test host that uses this idiom, so
	// unrelated test classes' WebApplicationFactory instances all end up racing
	// uncoordinated schema-create + writes against the exact same physical SQLite files —
	// most commonly logs/$system/petbox.db (the self-log, auto-created at startup whenever
	// Features:Logging is on, which is the Testing-environment default) and, for the
	// log-pipeline tests, logs/$system/default.db. That's the mechanism behind the
	// intermittent "no such table" log-pipeline flake and (suspected on Linux CI, where
	// SQLite's POSIX advisory locking is weaker than Windows' mandatory locking) the
	// LogPipelineTests exit-134 SIGABRT: concurrent, uncoordinated DDL/writes to one file
	// from many independent ScopedDbFactory instances. Giving each call its own directory
	// isolates every derived module directory per test host, same as the Core db file itself.
	public static string NewTempConnectionString(string prefix = "petbox-test")
	{
		var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		return $"Data Source={Path.Combine(dir, "petbox.db")};Cache=Shared";
	}
}
