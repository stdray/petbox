using Microsoft.Data.Sqlite;

namespace PetBox.Memory.Data;

// Lazy schema bootstrap for a per-store SQLite file. Passed to
// ScopedDbFactory<MemoryDb> as the ensure-schema delegate; idempotent. Shape =
// TemporalRow base columns + MemoryEntry payload.
public static class MemorySchema
{
	public static void Ensure(string connectionString)
	{
		using var raw = new SqliteConnection(connectionString);
		raw.Open();
		using var cmd = raw.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS memory_entries (
				Key         TEXT    NOT NULL,
				Version     INTEGER NOT NULL,
				Description TEXT    NOT NULL,
				Body        TEXT    NOT NULL,
				Tags        TEXT    NOT NULL,
				PrevKey     TEXT,
				ActiveFrom  INTEGER NOT NULL,
				ActiveTo    INTEGER,
				Created     TEXT    NOT NULL,
				Updated     TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			CREATE INDEX IF NOT EXISTS ix_memory_entries_active ON memory_entries (ActiveTo, Key);
			""";
		cmd.ExecuteNonQuery();
	}
}
