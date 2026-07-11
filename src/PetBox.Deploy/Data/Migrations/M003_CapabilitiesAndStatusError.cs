using FluentMigrator;

namespace PetBox.Deploy.Data.Migrations;

// Agent-reported node capabilities (docker/caddy, CSV) + a per-service status error so
// a failed reconcile (e.g. "site assigned but caddy is not available") is an explicit
// status, not a silent stderr line on the node.
//
// Plain typed ADD COLUMNs. The `Column(...).Exists()` guards that used to wrap them are gone —
// see M002 for why (they guarded a race that cannot happen and could not have been rescued that
// way if it did; both tables are M001's, and VersionInfo guarantees M001 ran).
[Migration(3, "Add deploy_node.Capabilities + deploy_deployment_status.Error")]
public sealed class M003_CapabilitiesAndStatusError : Migration
{
	public override void Up()
	{
		Alter.Table("deploy_node")
			.AddColumn("Capabilities").AsString().NotNullable().WithDefaultValue("");

		Alter.Table("deploy_deployment_status")
			.AddColumn("Error").AsString().Nullable();
	}

	public override void Down()
	{
		Delete.Column("Capabilities").FromTable("deploy_node");
		Delete.Column("Error").FromTable("deploy_deployment_status");
	}
}
