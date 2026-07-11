using FluentMigrator;

namespace PetBox.Deploy.Data.Migrations;

// Baseline deploy schema: fleet node registry, per-(service,node) desired-state, and
// the agent-reported actual-state. Enums stored as INTEGER. One active deployment per
// (Service, NodeId) is enforced by a unique index (a service has at most one copy per
// node — the invariant that keeps placement port-conflict-free).
//
// All of it is expressible with the typed API — tables (incl. the composite PK on
// deploy_deployment_status, spelled as two `.PrimaryKey()` columns) and both indexes, one of them
// UNIQUE but NOT partial. So there is no raw DDL here and no need for SqliteDdl.
//
// The `IF NOT EXISTS` this migration used to carry is gone. It never protected anything: a
// migration runs exactly once, gated by VersionInfo, and the deploy file's schema has been born in
// migrations from day one (nothing creates these tables at runtime — there is nothing to "adopt").
// All a tolerant CREATE could ever do here is swallow a schema divergence instead of reporting it.
[Migration(1, "Create deploy node/deployment/deployment_status tables")]
public sealed class M001_Deploy : Migration
{
	public override void Up()
	{
		Create.Table("deploy_node")
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("DisplayName").AsString().NotNullable()
			.WithColumn("Tags").AsString().NotNullable()
			.WithColumn("Ephemeral").AsInt64().NotNullable()
			.WithColumn("KeyRef").AsString().Nullable()
			.WithColumn("LastSeenAt").AsString().Nullable()
			.WithColumn("CreatedAt").AsString().NotNullable();

		Create.Table("deploy_deployment")
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("Service").AsString().NotNullable()
			.WithColumn("Project").AsString().NotNullable()
			.WithColumn("NodeId").AsString().NotNullable()
			.WithColumn("ImageDigest").AsString().NotNullable()
			.WithColumn("DesiredState").AsInt64().NotNullable()
			.WithColumn("Relocatable").AsInt64().NotNullable()
			.WithColumn("RequiredTags").AsString().NotNullable()
			.WithColumn("ConfigTags").AsString().NotNullable()
			.WithColumn("ConfigHash").AsString().NotNullable()
			.WithColumn("UpdatedAt").AsString().NotNullable();

		Create.Index("ix_deploy_deployment_node").OnTable("deploy_deployment")
			.OnColumn("NodeId").Ascending();

		// The placement invariant: at most one deployment of a service per node.
		Create.Index("ux_deploy_deployment_service_node").OnTable("deploy_deployment")
			.OnColumn("Service").Ascending()
			.OnColumn("NodeId").Ascending()
			.WithOptions().Unique();

		Create.Table("deploy_deployment_status")
			.WithColumn("NodeId").AsString().NotNullable().PrimaryKey()
			.WithColumn("Service").AsString().NotNullable().PrimaryKey()
			.WithColumn("ActualState").AsInt64().NotNullable()
			.WithColumn("ContainerId").AsString().Nullable()
			.WithColumn("ImageDigest").AsString().Nullable()
			.WithColumn("Healthy").AsInt64().NotNullable()
			.WithColumn("ReportedAt").AsString().NotNullable();
	}

	// No `IF EXISTS`: Up() created all three (indexes go with their table), so Down() finds them.
	public override void Down()
	{
		Delete.Table("deploy_deployment_status");
		Delete.Table("deploy_deployment");
		Delete.Table("deploy_node");
	}
}
