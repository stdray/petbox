using Microsoft.Data.Sqlite;

namespace PetBox.Core.Data;

// Operational PRAGMAs applied to internal per-file SQLite databases
// (Tasks/Memory/Sessions/Logs tiers) before their FluentMigrator schema runs.
//
// journal_mode = WAL persists in the file header (set once, survives reopen) and
// lets readers run concurrently with a writer. busy_timeout is per-connection;
// we set it here on the bootstrap connection, and Microsoft.Data.Sqlite's command
// timeout drives SQLITE_BUSY retries on the linq2db connections that follow.
//
// `tier` is required rather than defaulted, and that is the point: synchronous is per-connection
// like busy_timeout, so the caller has to say which durability class this file belongs to. A
// default here would let a new tier acquire a durability nobody picked — the exact state
// SqliteTier was introduced to end.
public static class SqlitePragmas
{
	public const int DefaultBusyTimeoutMs = 5000;

	public static void ApplyWal(string connectionString, SqliteTier tier, int busyTimeoutMs = DefaultBusyTimeoutMs)
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
		// This bootstrap connection is short-lived, but it is the one the very first schema build
		// runs on. It covers ITSELF only — synchronous does not persist into the file the way
		// journal_mode above does, so the tier's real coverage comes from the per-open hook on the
		// connections that follow, not from this line.
		SqliteDurability.ApplyTo(raw, tier);
	}
}
