using Microsoft.Data.Sqlite;

namespace PetBox.Tasks.Data;

// Lazy schema bootstrap for a per-board SQLite file. Passed to
// ScopedDbFactory<TasksDb> as the ensure-schema delegate; idempotent. The shape
// matches TemporalRow (Key/Version/ActiveFrom/ActiveTo/PrevKey/Created/Updated)
// plus the PlanNode payload.
public static class TasksSchema
{
	public static void Ensure(string connectionString)
	{
		using var raw = new SqliteConnection(connectionString);
		raw.Open();
		using var cmd = raw.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS plan_nodes (
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Status     INTEGER NOT NULL,
				Body       TEXT    NOT NULL,
				CommitRef  TEXT,
				Priority   INTEGER NOT NULL DEFAULT 0,
				PrevKey    TEXT,
				ActiveFrom INTEGER NOT NULL,
				ActiveTo   INTEGER,
				Created    TEXT    NOT NULL,
				Updated    TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			CREATE INDEX IF NOT EXISTS ix_plan_nodes_active ON plan_nodes (ActiveTo, Priority, Key);
			""";
		cmd.ExecuteNonQuery();
	}
}
