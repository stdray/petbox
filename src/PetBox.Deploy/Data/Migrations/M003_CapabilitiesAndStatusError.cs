using FluentMigrator;

namespace PetBox.Deploy.Data.Migrations;

// Agent-reported node capabilities (docker/caddy, CSV) + a per-service status error so
// a failed reconcile (e.g. "site assigned but caddy is not available") is an explicit
// status, not a silent stderr line on the node.
[Migration(3, "Add deploy_node.Capabilities + deploy_deployment_status.Error")]
public sealed class M003_CapabilitiesAndStatusError : Migration
{
	public override void Up()
	{
		// SQLite's ADD COLUMN has no IF NOT EXISTS — guard on the actual schema (see M002).
		if (!Schema.Table("deploy_node").Column("Capabilities").Exists())
			Execute.Sql("ALTER TABLE deploy_node ADD COLUMN Capabilities TEXT NOT NULL DEFAULT '';");
		if (!Schema.Table("deploy_deployment_status").Column("Error").Exists())
			Execute.Sql("ALTER TABLE deploy_deployment_status ADD COLUMN Error TEXT;");
	}

	public override void Down() => Execute.Sql("""
		ALTER TABLE deploy_node DROP COLUMN Capabilities;
		ALTER TABLE deploy_deployment_status DROP COLUMN Error;
		""");
}
