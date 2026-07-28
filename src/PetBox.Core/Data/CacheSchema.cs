namespace PetBox.Core.Data;

// Schema bootstrap for the single disk-cache file. Same three-line shape as TasksSchema /
// MemorySchema / DeploySchema — the Core invariants, then this tier's FluentMigrator set — with one
// addition the data-holding tiers do not need.
//
// THE ADDITION, and why it must come FIRST: auto_vacuum is only settable while the database is
// still empty, and FluentMigrator's very first act is to create its VersionInfo table. Run it after
// the migrations and SQLite ignores the pragma silently, leaving a cache file that grows to its
// high-water mark and never comes back down — which is precisely the measured defect that
// disqualified NeoSmart.Caching.Sqlite. See SqlitePragmas.ApplyAutoVacuumIncremental for the other
// half (the periodic incremental_vacuum, run by the cache's sweep).
//
// Idempotent: called once at startup, and safe to call again.
public static class CacheSchema
{
	public static void Ensure(string connectionString)
	{
		SqlitePragmas.ApplyAutoVacuumIncremental(connectionString);
		// SqliteTier.Derived — synchronous=NORMAL. Everything in this file is reconstructible from
		// the source it was computed from, so a power cut that rolls back the last commits costs a
		// cache MISS, not data. NORMAL is safe here for the same reason it is safe for the log tier
		// and no other: the ApplyWal on the very next line. Under journal_mode=DELETE it would risk
		// corruption rather than a lost tail — and a corrupt cache file is not self-healing just
		// because its contents are disposable, since a reader hits the corruption before it ever
		// gets to decide the entry is a miss.
		SqlitePragmas.ApplyWal(connectionString, SqliteTier.Derived);
		// Namespace-scoped: the cache set and the core.db set share the PetBox.Core assembly, so an
		// assembly-wide scan would build every core.db table inside the cache file.
		MigrationRunner.Run(
			connectionString,
			typeof(CacheMigrations.M1001_CacheEntries).Assembly,
			SqliteTier.Derived,
			typeof(CacheMigrations.M1001_CacheEntries).Namespace);
	}

	// THE connection string for a cache file at `dbPath`, in one place so the factory and the
	// bootstrap cannot drift apart.
	//
	// Bare `Data Source=` — deliberately NOT WithSharedCache. core.db already burned this project on
	// Cache=Shared's SQLITE_LOCKED, which the busy handler does not retry (AGENTS.md, "Database
	// connections"); here only the WAL file and OS byte-range locks coordinate writers, and the
	// SQLITE_BUSY that produces IS retried.
	//
	// Plus an explicit Default Timeout, which is the one knob that actually bounds the wait — see
	// SqliteConnectionStrings.WithDefaultTimeout for the measurement. A cache must fail fast and
	// degrade to a miss; holding a request for ADO.NET's 30-second default would make the cache the
	// slowest thing in the system exactly when it is least useful.
	public static string ConnectionString(string dbPath) =>
		SqliteConnectionStrings.WithDefaultTimeout(SqliteConnectionStrings.ForFile(dbPath));
}
