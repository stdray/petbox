using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Plans move from one-file-per-board to one-file-per-project: a project's boards now share a
// single plan_nodes table, partitioned by a Board column. Two boards each run an independent
// per-board version cursor, so (Key, Version) collides across boards — the PRIMARY KEY must
// become (Board, Key, Version), and active-key uniqueness must be per-board. SQLite cannot alter
// a PK in place, so rebuild the table (same technique as M004), defaulting existing rows' Board
// to '' (a fresh per-project file is empty here; the one-time data migrator stamps Board as it
// copies rows in from the legacy per-board files). Forward-only.
//
// STYLE (see M004): the new table, the DROP and the RENAME are typed DDL; only the INSERT..SELECT
// that moves the rows has no typed form and goes through the NAMED, guarded SqliteDdl.Raw, and
// only the partial unique index goes through SqliteDdl.PartialIndex.
[Migration(5, "plan_nodes: add Board, repoint PK to (Board, Key, Version), per-board indexes")]
public sealed class M005_BoardColumn : SqliteMigration
{
	public override void Up()
	{
		Create.Table("plan_nodes_new")
			.WithColumn("Board").AsString().NotNullable().WithDefaultValue("").PrimaryKey()
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Status").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("Name").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("Body").AsString().NotNullable()
			.WithColumn("CommitRef").AsString().Nullable()
			.WithColumn("Priority").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable()
			.WithColumn("Type").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("NodeId").AsString().NotNullable().WithDefaultValue("");

		SqliteDdl.Raw(
			"table rebuild: move the plan_nodes rows into the new (Board, Key, Version) shape — SQLite " +
			"cannot alter a PK in place, and an INSERT..SELECT has no typed form. A fresh per-project file " +
			"is empty here; a legacy per-board file adopted in place carries its rows over with an empty Board",
			"""
			INSERT INTO plan_nodes_new (Board,Key,Version,Status,Name,Body,CommitRef,Priority,PrevKey,ActiveFrom,ActiveTo,Created,Updated,Type,NodeId)
			SELECT '', Key, Version, Status, Name, Body, CommitRef, Priority, PrevKey, ActiveFrom, ActiveTo, Created, Updated, Type, NodeId
			FROM plan_nodes;
			""");

		// No `IF EXISTS`: plan_nodes exists — M001 created it, M004 rebuilt it, VersionInfo guarantees
		// both ran. Its indexes are dropped with it and recreated below in their per-board shape.
		Delete.Table("plan_nodes");
		Rename.Table("plan_nodes_new").To("plan_nodes");

		Create.Index("ix_plan_nodes_active").OnTable("plan_nodes")
			.OnColumn("Board").Ascending()
			.OnColumn("ActiveTo").Ascending()
			.OnColumn("Priority").Ascending()
			.OnColumn("Key").Ascending();

		// PARTIAL unique index: at most one ACTIVE revision per key, PER BOARD.
		SqliteDdl.PartialIndex(
			name: "ux_plan_nodes_active_board_key",
			table: "plan_nodes",
			columns: ["Board", "Key"],
			where: "ActiveTo IS NULL",
			unique: true);
	}

	public override void Down() { } // forward-only
}
