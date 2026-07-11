using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Per-board temporal plan-node table, plus the partial unique index that keeps the temporal model
// honest: at most ONE active revision (ActiveTo IS NULL) per Key. That index is what turns the
// concurrent-insert race (critic C1) into a catchable constraint violation instead of a silent
// double-active row. M004/M005/M011 later rebuild this table (Status -> TEXT, Board partition,
// CommitRef dropped); this is its birth shape.
//
// The table and the plain index are typed FluentMigrator DDL. The partial unique index is the one
// thing the typed API cannot express, so it goes through SqliteDdl.PartialIndex — named, and
// guarded to FAIL on a non-SQLite engine rather than quietly degrade into a TOTAL unique index
// (which would forbid history outright).
//
// This migration used to carry `IF NOT EXISTS`, to adopt files created by the old hand-DDL
// TasksSchema.Ensure. That is gone: a migration runs exactly once, gated by VersionInfo, so a
// tolerant CREATE never protected anything — it only stood ready to swallow a schema divergence.
[Migration(1, "Create plan_nodes temporal table + unique-active-key index")]
public sealed class M001_PlanNodes : SqliteMigration
{
	public override void Up()
	{
		Create.Table("plan_nodes")
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
			.WithColumn("Updated").AsString().NotNullable();

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
	}

	// No `IF EXISTS`: Up() created the table, so Down() finds it (its indexes go with it).
	public override void Down() => Delete.Table("plan_nodes");
}
