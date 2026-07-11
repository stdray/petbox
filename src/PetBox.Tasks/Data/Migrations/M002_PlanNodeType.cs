using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Adds the workflow `Type` column (feature|bug on work boards; empty elsewhere).
// `Status` stays declared INTEGER in M001 but now holds workflow SLUG strings —
// SQLite's type affinity stores non-numeric text as TEXT, so fresh boards work
// as-is. Legacy prod data (integer statuses) is handled out-of-band by a
// backup-copy script, not an in-place rewrite (per project decision).
//
// ALTER TABLE ADD COLUMN is expressible in the typed API, so it is written there — the raw
// Execute.Sql it used to be said nothing that Alter.Table does not say, and hid the operation
// from the runner's expression model.
[Migration(2, "Add Type column to plan_nodes (workflow task type)")]
public sealed class M002_PlanNodeType : Migration
{
	public override void Up() =>
		Alter.Table("plan_nodes")
			.AddColumn("Type").AsString().NotNullable().WithDefaultValue("");

	// No `IF EXISTS`: Up() added the column, so Down() finds it.
	public override void Down() => Delete.Column("Type").FromTable("plan_nodes");
}
