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
	}
}
