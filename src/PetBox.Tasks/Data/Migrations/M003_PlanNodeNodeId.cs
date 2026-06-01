using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Adds the stable NodeId column. New nodes get a fresh id; edits/renames carry the
// prior row's id (done in the upsert layer). Relations reference NodeId, so links
// don't rot on re-key. Existing rows default to '' (backfilled lazily on next edit;
// legacy prod data is handled by the backup-copy script per project decision).
[Migration(3, "Add stable NodeId column to plan_nodes")]
public sealed class M003_PlanNodeNodeId : Migration
{
	public override void Up() =>
		Execute.Sql("ALTER TABLE plan_nodes ADD COLUMN NodeId TEXT NOT NULL DEFAULT '';");

	public override void Down() =>
		Execute.Sql("ALTER TABLE plan_nodes DROP COLUMN NodeId;");
}
