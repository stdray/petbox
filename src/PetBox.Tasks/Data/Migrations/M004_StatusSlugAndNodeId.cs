using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Migrates legacy per-board files (lazily, on first open under the new code — no central DB, no
// hand-run script, no per-project access). Legacy files declared `Status` as INTEGER (old
// PlanStatus enum); linq2db picks its reader from the DECLARED column type, so text slugs in an
// INTEGER column read back as 0. The only robust fix is to change the column TYPE, which SQLite
// does via a table rebuild:
//   - rebuild plan_nodes with Status TEXT (+ Type/NodeId from M002/M003);
//   - copy ALL revisions (history preserved), remapping the integer enum {0..5} -> slug;
//   - give each ACTIVE node a stable NodeId.
// On a fresh file the table is empty at this point and Status is already TEXT, so the CASE
// preserves text values and the rebuild is a trivial no-op. Forward-only.
//
// STYLE. The rebuild is split along the line the typed API can actually hold:
//   * the NEW table is typed DDL (Create.Table) — it is an ordinary CREATE TABLE and there is no
//     reason to hand-write it;
//   * the DROP and the RENAME are typed too (Delete.Table / Rename.Table);
//   * the two things with no typed form go through NAMED, guarded SqliteDdl helpers instead of an
//     anonymous Execute.Sql — the INSERT..SELECT status remap and the NodeId backfill are DML
//     (Insert.IntoTable takes literal rows only, Update.Table takes literal values only, and both
//     of these are SQLite expressions: typeof(), randomblob(), hex()), and the partial unique
//     index has no typed form at all.
// Each statement is its own expression, as before: a schema change must land before the next
// statement is prepared, or SQLite resolves columns against a stale schema.
[Migration(4, "Rebuild plan_nodes: Status INTEGER->TEXT slug; backfill NodeId")]
public sealed class M004_StatusSlugAndNodeId : SqliteMigration
{
	public override void Up()
	{
		// The rebuilt shape: same columns as M001 + Type (M002) + NodeId (M003), but Status is now
		// declared TEXT. The PK is unchanged; M005 repoints it to (Board, Key, Version).
		Create.Table("plan_nodes_new")
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
			"table rebuild: copy every revision into the TEXT-Status shape, remapping the legacy integer " +
			"PlanStatus enum {0..5} to its slug. SQLite cannot change a column's declared type in place, and " +
			"this is an INSERT..SELECT with a CASE over typeof(Status) — DML with a SQLite-specific type " +
			"predicate, which Insert.IntoTable (literal rows only) cannot express",
			"""
			INSERT INTO plan_nodes_new (Key,Version,Status,Name,Body,CommitRef,Priority,PrevKey,ActiveFrom,ActiveTo,Created,Updated,Type,NodeId)
			SELECT Key, Version,
				CASE WHEN typeof(Status) = 'integer' THEN
					CASE CAST(Status AS INTEGER)
						WHEN 0 THEN 'Pending' WHEN 1 THEN 'InProgress' WHEN 2 THEN 'Done'
						WHEN 3 THEN 'Blocked' WHEN 4 THEN 'Deferred' WHEN 5 THEN 'Cancelled' ELSE '' END
				ELSE Status END,
				Name, Body, CommitRef, Priority, PrevKey, ActiveFrom, ActiveTo, Created, Updated, Type, NodeId
			FROM plan_nodes;
			""");

		// No `IF EXISTS` on the DROP: plan_nodes is created by M001, which VersionInfo guarantees ran.
		// Its indexes go down with it, so the two below are recreated on the new table, not adopted —
		// which is why they no longer carry `IF NOT EXISTS` either.
		Delete.Table("plan_nodes");
		Rename.Table("plan_nodes_new").To("plan_nodes");

		Create.Index("ix_plan_nodes_active").OnTable("plan_nodes")
			.OnColumn("ActiveTo").Ascending()
			.OnColumn("Priority").Ascending()
			.OnColumn("Key").Ascending();

		SqliteDdl.PartialIndex(
			name: "ux_plan_nodes_active_key",
			table: "plan_nodes",
			columns: ["Key"],
			where: "ActiveTo IS NULL",
			unique: true);

		SqliteDdl.Raw(
			"backfill: stamp a stable NodeId on every ACTIVE node that has none. The value is generated " +
			"per row by SQLite itself (lower(hex(randomblob(16)))), so it is an UPDATE with a SQL " +
			"expression — Update.Table can only set literal values",
			"UPDATE plan_nodes SET NodeId = lower(hex(randomblob(16))) WHERE ActiveTo IS NULL AND (NodeId IS NULL OR NodeId = '');");
	}

	public override void Down() { } // forward-only data migration
}
