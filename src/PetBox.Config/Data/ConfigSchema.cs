using Microsoft.Data.Sqlite;

namespace PetBox.Config.Data;

// Lazy schema bootstrap for a per-workspace config SQLite file. Passed to
// ScopedDbFactory<ConfigDb> as the ensure-schema delegate; runs once per file on
// first open. Idempotent, with additive column migrations for schema evolution
// (never drops — see AddColumnIfMissing).
public static class ConfigSchema
{
	public static void Ensure(string connectionString)
	{
		using var raw = new SqliteConnection(connectionString);
		raw.Open();
		using (var cmd = raw.CreateCommand())
		{
			cmd.CommandText = """
				CREATE TABLE IF NOT EXISTS ConfigBindings (
					Id INTEGER PRIMARY KEY AUTOINCREMENT,
					Path TEXT NOT NULL,
					Value TEXT NOT NULL,
					Tags TEXT NOT NULL,
					Kind INTEGER NOT NULL DEFAULT 0,
					Ciphertext TEXT,
					Iv TEXT,
					AuthTag TEXT,
					Version INTEGER NOT NULL DEFAULT 1,
					ContentHash TEXT NOT NULL DEFAULT '',
					IsDeleted INTEGER NOT NULL DEFAULT 0,
					DeletedAt TEXT,
					CreatedAt TEXT NOT NULL,
					UpdatedAt TEXT NOT NULL
				);
				CREATE INDEX IF NOT EXISTS IX_ConfigBindings_Path ON ConfigBindings (Path);

				CREATE TABLE IF NOT EXISTS ConfigBindingHistory (
					Id INTEGER PRIMARY KEY AUTOINCREMENT,
					BindingId INTEGER NOT NULL,
					Action TEXT NOT NULL,
					Path TEXT NOT NULL,
					Tags TEXT NOT NULL,
					Kind INTEGER NOT NULL DEFAULT 0,
					OldValue TEXT,
					NewValue TEXT,
					Actor TEXT NOT NULL DEFAULT 'system',
					At TEXT NOT NULL
				);
				CREATE INDEX IF NOT EXISTS IX_ConfigBindingHistory_At ON ConfigBindingHistory (At DESC);
				CREATE INDEX IF NOT EXISTS IX_ConfigBindingHistory_Path ON ConfigBindingHistory (Path);

				CREATE TABLE IF NOT EXISTS TagVocabulary (
					Id INTEGER PRIMARY KEY AUTOINCREMENT,
					TagKey TEXT NOT NULL UNIQUE,
					Description TEXT,
					CreatedAt TEXT NOT NULL
				);
				""";
			cmd.ExecuteNonQuery();
		}

		AddColumnIfMissing(raw, "ConfigBindings", "Kind", "INTEGER NOT NULL DEFAULT 0");
		AddColumnIfMissing(raw, "ConfigBindings", "Ciphertext", "TEXT");
		AddColumnIfMissing(raw, "ConfigBindings", "Iv", "TEXT");
		AddColumnIfMissing(raw, "ConfigBindings", "AuthTag", "TEXT");
		AddColumnIfMissing(raw, "ConfigBindings", "Version", "INTEGER NOT NULL DEFAULT 1");
		AddColumnIfMissing(raw, "ConfigBindings", "ContentHash", "TEXT NOT NULL DEFAULT ''");
		AddColumnIfMissing(raw, "ConfigBindings", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
		AddColumnIfMissing(raw, "ConfigBindings", "DeletedAt", "TEXT");

		using (var idx = raw.CreateCommand())
		{
			idx.CommandText = "CREATE INDEX IF NOT EXISTS IX_ConfigBindings_IsDeleted ON ConfigBindings (IsDeleted);";
			idx.ExecuteNonQuery();
		}
	}

	static void AddColumnIfMissing(SqliteConnection raw, string table, string column, string definition)
	{
		using var check = raw.CreateCommand();
		check.CommandText = $"PRAGMA table_info({table})";
		using var reader = check.ExecuteReader();
		while (reader.Read())
		{
			if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
				return;
		}
		reader.Close();
		using var alter = raw.CreateCommand();
		alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
		alter.ExecuteNonQuery();
	}
}
