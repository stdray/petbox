using FluentMigrator;

namespace PetBox.Deploy.Data.Migrations;

// The agent's host snapshot (security posture / memory / disk / os, JSON) on the node
// row, refreshed by heartbeat. Warnings are computed from it at read time, not stored.
[Migration(4, "Add deploy_node.HostReport (agent host snapshot, JSON)")]
public sealed class M004_HostReport : Migration
{
	public override void Up()
	{
		// SQLite's ADD COLUMN has no IF NOT EXISTS — guard on the actual schema (see M002).
		if (!Schema.Table("deploy_node").Column("HostReport").Exists())
			Execute.Sql("ALTER TABLE deploy_node ADD COLUMN HostReport TEXT NOT NULL DEFAULT '{}';");
	}

	public override void Down() => Execute.Sql(
		"ALTER TABLE deploy_node DROP COLUMN HostReport;");
}
