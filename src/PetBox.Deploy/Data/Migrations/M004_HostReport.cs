using FluentMigrator;

namespace PetBox.Deploy.Data.Migrations;

// The agent's host snapshot (security posture / memory / disk / os, JSON) on the node
// row, refreshed by heartbeat. Warnings are computed from it at read time, not stored.
//
// Plain typed ADD COLUMN. The `Column(...).Exists()` guard that used to wrap it is gone — see M002
// for why (deploy_node is M001's, and VersionInfo guarantees M001 ran).
[Migration(4, "Add deploy_node.HostReport (agent host snapshot, JSON)")]
public sealed class M004_HostReport : Migration
{
	public override void Up() =>
		Alter.Table("deploy_node")
			.AddColumn("HostReport").AsString().NotNullable().WithDefaultValue("{}");

	public override void Down() =>
		Delete.Column("HostReport").FromTable("deploy_node");
}
