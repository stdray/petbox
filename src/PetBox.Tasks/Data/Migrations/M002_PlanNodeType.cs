using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Adds the workflow `Type` column (feature|bug on work boards; empty elsewhere).
// `Status` stays declared INTEGER in M001 but now holds workflow SLUG strings —
// SQLite's type affinity stores non-numeric text as TEXT, so fresh boards work
// as-is. Legacy prod data (integer statuses) is handled out-of-band by a
// backup-copy script, not an in-place rewrite (per project decision).
[Migration(2, "Add Type column to plan_nodes (workflow task type)")]
public sealed class M002_PlanNodeType : Migration
{
	public override void Up() =>
		Execute.Sql("ALTER TABLE plan_nodes ADD COLUMN Type TEXT NOT NULL DEFAULT '';");

	public override void Down() =>
		Execute.Sql("ALTER TABLE plan_nodes DROP COLUMN Type;");
}
