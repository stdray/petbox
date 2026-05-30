using Microsoft.Data.Sqlite;

namespace PetBox.Sessions.Data;

// Lazy schema bootstrap for a per-project sessions file. Idempotent. Shape =
// TemporalRow base columns + SessionRow payload.
public static class SessionsSchema
{
	public static void Ensure(string connectionString)
	{
		using var raw = new SqliteConnection(connectionString);
		raw.Open();
		using var cmd = raw.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS sessions (
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Agent      TEXT    NOT NULL,
				Content    TEXT    NOT NULL,
				PrevKey    TEXT,
				ActiveFrom INTEGER NOT NULL,
				ActiveTo   INTEGER,
				Created    TEXT    NOT NULL,
				Updated    TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			CREATE INDEX IF NOT EXISTS ix_sessions_active ON sessions (ActiveTo, Key);
			""";
		cmd.ExecuteNonQuery();
	}
}
