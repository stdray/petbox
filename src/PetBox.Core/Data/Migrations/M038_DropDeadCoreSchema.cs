using System.Data;
using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Archaeology cleanup (w3-drop-dead-core-schema): ConfigBindings (M002) and LogEntries (M003)
// were created in the CORE db (petbox.db) by early iterations. Both storages have since moved
// out of core: config bindings live in the per-workspace config/{ws}.db (PetBox.Config ConfigDb
// + ConfigSchema), log entries live in the per-log logs/{project}/{log}.db (PetBox.Log.Core LogDb
// + LogSchema). Neither table is mapped on PetBoxDb and no code reads or writes the core copies —
// they are dead weight in every core db (and in the migrated test template).
//
// Safety on live prod: the drops are guarded twice — the table is dropped only if it exists AND
// is empty. If a core copy somehow holds rows, the drop is SKIPPED (not failed): a surprise row
// must never break a deploy, and keeping an unused table costs nothing. The leftover table then
// shows up as "still there" and can be dealt with deliberately. SQLite drops the table's indexes
// (IX_ConfigBindings_Path_Tags, IX_LogEntries_*) together with the table.
//
// M002/M003 themselves are left untouched — their versions are already applied on prod.
// Forward-only: Down does not recreate these tables (nothing would use them).
[Migration(38, "Drop dead core tables ConfigBindings/LogEntries (real data lives in config/*.db and logs/**.db)")]
public sealed class M038_DropDeadCoreSchema : Migration
{
	static readonly string[] DeadTables = ["ConfigBindings", "LogEntries"];

	public override void Up() => Execute.WithConnection((conn, tx) =>
	{
		foreach (var table in DeadTables)
		{
			if (!TableExists(conn, tx, table)) continue;
			if (RowCount(conn, tx, table) > 0) continue; // non-empty: leave it, don't break the deploy

			using var drop = conn.CreateCommand();
			drop.Transaction = tx;
			drop.CommandText = $"DROP TABLE \"{table}\";";
			drop.ExecuteNonQuery();
		}
	});

	public override void Down() { } // forward-only

	static bool TableExists(IDbConnection conn, IDbTransaction tx, string table)
	{
		using var cmd = conn.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
		var p = cmd.CreateParameter();
		p.ParameterName = "@name";
		p.Value = table;
		cmd.Parameters.Add(p);
		return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
	}

	// Table name is a hard-coded literal from DeadTables — no injection surface.
	static long RowCount(IDbConnection conn, IDbTransaction tx, string table)
	{
		using var cmd = conn.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = $"SELECT count(*) FROM \"{table}\";";
		return Convert.ToInt64(cmd.ExecuteScalar());
	}
}
