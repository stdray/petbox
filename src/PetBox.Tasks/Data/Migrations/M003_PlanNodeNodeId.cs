using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Adds the stable NodeId column. New nodes get a fresh id; edits/renames carry the
// prior row's id (done in the upsert layer). Relations reference NodeId, so links
// don't rot on re-key. Existing rows default to '' (M004 backfills the active ones;
// legacy prod data is handled by the backup-copy script per project decision).
//
// ALTER TABLE ADD COLUMN is expressible in the typed API — see M002.
[Migration(3, "Add stable NodeId column to plan_nodes")]
public sealed class M003_PlanNodeNodeId : Migration
{
	public override void Up() =>
		Alter.Table("plan_nodes")
			.AddColumn("NodeId").AsString().NotNullable().WithDefaultValue("");

	// No `IF EXISTS`: Up() added the column, so Down() finds it.
	public override void Down() => Delete.Column("NodeId").FromTable("plan_nodes");
}
