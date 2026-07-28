using Microsoft.Data.Sqlite;

namespace PetBox.Core.Data;

// Operational PRAGMAs applied to internal per-file SQLite databases
// (Tasks/Memory/Sessions/Logs tiers) before their FluentMigrator schema runs.
//
// journal_mode = WAL persists in the file header (set once, survives reopen) and
// lets readers run concurrently with a writer. busy_timeout is per-connection;
// we set it here on the bootstrap connection, and Microsoft.Data.Sqlite's command
// timeout drives SQLITE_BUSY retries on the linq2db connections that follow.
public static class SqlitePragmas
{
	public const int DefaultBusyTimeoutMs = 5000;

	public static void ApplyWal(string connectionString, int busyTimeoutMs = DefaultBusyTimeoutMs)
	{
		using var raw = new SqliteConnection(connectionString);
		raw.Open();

		using (var pragma = raw.CreateCommand())
		{
			// journal_mode returns the new mode; ExecuteScalar ensures it's applied.
			pragma.CommandText = "PRAGMA journal_mode = WAL;";
			pragma.ExecuteScalar();
		}
		using (var pragma = raw.CreateCommand())
		{
			pragma.CommandText = $"PRAGMA busy_timeout = {busyTimeoutMs};";
			pragma.ExecuteNonQuery();
		}
		// No-op in production (SqliteDurability.Relaxed is null there). This bootstrap connection
		// is short-lived, but it is the one the very first schema build runs on.
		SqliteDurability.ApplyTo(raw);
	}

	// Puts a database into INCREMENTAL auto-vacuum. MUST run before the file has any schema:
	// SQLite only accepts an auto_vacuum MODE CHANGE on an empty database — on a populated one the
	// pragma is silently ignored and only a full VACUUM can change it. Unlike busy_timeout this is
	// persistent (it lives in the file header), so it is set once at bootstrap like journal_mode.
	//
	// Callers must ALSO run `PRAGMA incremental_vacuum` periodically, and that is not a detail: on
	// its own auto_vacuum=INCREMENTAL merely moves freed pages onto a free list and never returns a
	// byte to the filesystem. Skipping the second half is exactly the defect that disqualified
	// NeoSmart.Caching.Sqlite — its file still measured 28,176,384 bytes after its 500 rows had gone
	// to 0. With both halves the same stand took a cache file from 27 MB back down to 20 KB.
	//
	// Only the disk cache uses this (see CacheSchema). The tiers that hold DATA deliberately do not:
	// their files do not churn their whole contents on a TTL, so the page-relocation cost buys them
	// nothing.
	public static void ApplyAutoVacuumIncremental(string connectionString)
	{
		using var raw = new SqliteConnection(connectionString);
		raw.Open();
		using var pragma = raw.CreateCommand();
		// Returns the resulting mode; ExecuteScalar ensures it is applied.
		pragma.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
		pragma.ExecuteScalar();
	}

	// The other half: hand the pages freed by expired rows back to the filesystem.
	public const string IncrementalVacuumStatement = "PRAGMA incremental_vacuum;";
}
