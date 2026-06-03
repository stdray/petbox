using Microsoft.Data.Sqlite;

namespace PetBox.Tests.Data;

// A1 invariants for the internal temporal tiers (Tasks/Memory/Sessions): the
// FluentMigrator-backed *Schema.Ensure is idempotent, sets WAL, and installs the
// partial unique index that allows at most one active revision (ActiveTo IS NULL)
// per Key — turning the concurrent-insert race (critic C1) into a catchable error.
public sealed class TemporalSchemaInvariantsTests : IDisposable
{
	readonly string _dir;

	public TemporalSchemaInvariantsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-schema-inv-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Theory]
	[InlineData("tasks", "plan_nodes", "ux_plan_nodes_active_board_key")]
	[InlineData("memory", "memory_entries", "ux_memory_entries_active_key")]
	[InlineData("sessions", "sessions", "ux_sessions_active_key")]
	public void Ensure_IsIdempotent_SetsWal_AndCreatesPartialUniqueIndex(string tier, string table, string index)
	{
		var cs = $"Data Source={Path.Combine(_dir, tier + ".db")}";

		// Idempotent: running the tier's ensure twice must not throw (adopts the
		// file, records VersionInfo once, no-ops on the second pass).
		Ensure(tier, cs);
		Ensure(tier, cs);

		using var conn = new SqliteConnection(cs);
		conn.Open();

		// WAL persisted in the file header.
		using (var pragma = conn.CreateCommand())
		{
			pragma.CommandText = "PRAGMA journal_mode;";
			((string)pragma.ExecuteScalar()!).Should().Be("wal");
		}

		// The partial unique index exists.
		using (var idx = conn.CreateCommand())
		{
			idx.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name=@n;";
			idx.Parameters.AddWithValue("@n", index);
			Convert.ToInt64(idx.ExecuteScalar()).Should().Be(1);
		}

		// One active row for key 'k' inserts fine; a second active row for the same
		// key (different Version, ActiveTo IS NULL) violates the unique index.
		Exec(conn, $"INSERT INTO {table} {Columns(table)} VALUES {Values(table, version: 1)};");
		var ex = Assert.Throws<SqliteException>(() =>
			Exec(conn, $"INSERT INTO {table} {Columns(table)} VALUES {Values(table, version: 2)};"));
		ex.SqliteErrorCode.Should().Be(19); // SQLITE_CONSTRAINT
	}

	static void Ensure(string tier, string cs)
	{
		switch (tier)
		{
			case "tasks": PetBox.Tasks.Data.TasksSchema.Ensure(cs); break;
			case "memory": PetBox.Memory.Data.MemorySchema.Ensure(cs); break;
			case "sessions": PetBox.Sessions.Data.SessionsSchema.Ensure(cs); break;
		}
	}

	// Column lists / value tuples per tier (only NOT NULL columns without defaults
	// need values; Key+Version is the PK, ActiveTo left NULL = active revision).
	static string Columns(string table) => table switch
	{
		"plan_nodes" => "(Key, Version, Status, Body, ActiveFrom, Created, Updated)",
		"memory_entries" => "(Key, Version, Description, Body, Tags, ActiveFrom, Created, Updated)",
		"sessions" => "(Key, Version, Agent, Content, ActiveFrom, Created, Updated)",
		_ => throw new ArgumentOutOfRangeException(nameof(table)),
	};

	static string Values(string table, int version) => table switch
	{
		"plan_nodes" => $"('k', {version}, 0, 'b', {version}, 't', 't')",
		"memory_entries" => $"('k', {version}, 'd', 'b', '', {version}, 't', 't')",
		"sessions" => $"('k', {version}, 'claude', 'c', {version}, 't', 't')",
		_ => throw new ArgumentOutOfRangeException(nameof(table)),
	};

	static void Exec(SqliteConnection conn, string sql)
	{
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}
}
