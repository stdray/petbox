using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// commits[] replaces the single CommitRef column (node-commits-impl). A feature is usually several
// commits, so a node's commits become an SCD-2 set in a new plan_node_commits table (mirroring
// node_tag: NodeId + Board + a value + ValidFrom/ValidTo, active while ValidTo is null), attached
// to the node's stable NodeId so they survive renames. Indexes: on Sha for the reverse lookup (find
// nodes carrying a commit), on NodeId for the per-node read.
//
// Same migration, three moves — order matters:
//   1. create plan_node_commits;
//   2. seed it from the existing non-null CommitRef values (each active node's commit becomes one
//      active row, ValidFrom = the node's Created so the seeded timestamp round-trips);
//   3. rebuild plan_nodes WITHOUT the CommitRef column (SQLite cannot DROP COLUMN robustly under
//      older versions — the M004/M005 table-rebuild precedent).
// Forward-only.
//
// STYLE (see M004/M005): the tables, the DROP and the RENAME are typed DDL; only the two data moves
// (INSERT..SELECT — DML, and with SQLite's lower()/trim() in it) and the PARTIAL indexes have no
// typed form, and those go through the NAMED, guarded SqliteDdl helpers.
[Migration(11, "plan_node_commits (SCD-2) + seed from CommitRef + drop plan_nodes.CommitRef")]
public sealed class M011_PlanNodeCommits : SqliteMigration
{
	public override void Up()
	{
		// 1. the new temporal edge table.
		Create.Table("plan_node_commits")
			.WithColumn("NodeId").AsString().NotNullable().PrimaryKey()
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("Sha").AsString().NotNullable().PrimaryKey()
			.WithColumn("ValidFrom").AsString().NotNullable().PrimaryKey()
			.WithColumn("ValidTo").AsString().Nullable();

		SqliteDdl.PartialIndex("ix_plan_node_commits_sha", "plan_node_commits", ["Sha"], "ValidTo IS NULL");
		SqliteDdl.PartialIndex("ix_plan_node_commits_node", "plan_node_commits", ["NodeId"], "ValidTo IS NULL");

		// 2. seed from the active rows' non-empty CommitRef (normalized: trimmed + lowercased).
		//    Only active rows (ActiveTo IS NULL) with a stable NodeId carry a live commit.
		SqliteDdl.Raw(
			"seed: turn each active node's single CommitRef into one active plan_node_commits row. An " +
			"INSERT OR IGNORE .. SELECT (with lower()/trim() normalizing the sha) is DML — Insert.IntoTable " +
			"takes literal rows only, and has no OR IGNORE conflict clause",
			"""
			INSERT OR IGNORE INTO plan_node_commits (NodeId, Board, Sha, ValidFrom)
			SELECT NodeId, Board, lower(trim(CommitRef)), Created
			FROM plan_nodes
			WHERE ActiveTo IS NULL
			  AND NodeId IS NOT NULL AND NodeId <> ''
			  AND CommitRef IS NOT NULL AND trim(CommitRef) <> '';
			""");

		// 3. rebuild plan_nodes without CommitRef (table-rebuild — M004/M005 precedent).
		Create.Table("plan_nodes_new")
			.WithColumn("Board").AsString().NotNullable().WithDefaultValue("").PrimaryKey()
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Status").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("Name").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("Body").AsString().NotNullable()
			.WithColumn("Priority").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable()
			.WithColumn("Type").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("NodeId").AsString().NotNullable().WithDefaultValue("");

		SqliteDdl.Raw(
			"table rebuild: copy every revision across WITHOUT the CommitRef column (its content now lives " +
			"in plan_node_commits, seeded above). SQLite's DROP COLUMN is not robust across the versions we " +
			"support, and an INSERT..SELECT has no typed form",
			"""
			INSERT INTO plan_nodes_new (Board,Key,Version,Status,Name,Body,Priority,PrevKey,ActiveFrom,ActiveTo,Created,Updated,Type,NodeId)
			SELECT Board, Key, Version, Status, Name, Body, Priority, PrevKey, ActiveFrom, ActiveTo, Created, Updated, Type, NodeId
			FROM plan_nodes;
			""");

		// No `IF EXISTS`: plan_nodes exists (M001, rebuilt by M004 and M005 — all three gated by
		// VersionInfo). Its indexes are dropped with it and recreated below, so they no longer need
		// `IF NOT EXISTS` either. No triggers reference plan_nodes yet — M014 adds the first ones,
		// AFTER this rebuild.
		Delete.Table("plan_nodes");
		Rename.Table("plan_nodes_new").To("plan_nodes");

		Create.Index("ix_plan_nodes_active").OnTable("plan_nodes")
			.OnColumn("Board").Ascending()
			.OnColumn("ActiveTo").Ascending()
			.OnColumn("Priority").Ascending()
			.OnColumn("Key").Ascending();

		SqliteDdl.PartialIndex(
			name: "ux_plan_nodes_active_board_key",
			table: "plan_nodes",
			columns: ["Board", "Key"],
			where: "ActiveTo IS NULL",
			unique: true);
	}

	public override void Down() { } // forward-only
}
