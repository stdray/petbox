using FluentMigrator;

namespace PetBox.Deploy.Data.Migrations;

// Baseline deploy schema: fleet node registry, per-(service,node) desired-state, and
// the agent-reported actual-state. Enums stored as INTEGER. One active deployment per
// (Service, NodeId) is enforced by a unique index (a service has at most one copy per
// node — the invariant that keeps placement port-conflict-free).
[Migration(1, "Create deploy node/deployment/deployment_status tables")]
public sealed class M001_Deploy : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE TABLE IF NOT EXISTS deploy_node (
			Id          TEXT    NOT NULL PRIMARY KEY,
			DisplayName TEXT    NOT NULL,
			Tags        TEXT    NOT NULL,
			Ephemeral   INTEGER NOT NULL,
			KeyRef      TEXT,
			LastSeenAt  TEXT,
			CreatedAt   TEXT    NOT NULL
		);

		CREATE TABLE IF NOT EXISTS deploy_deployment (
			Id           TEXT    NOT NULL PRIMARY KEY,
			Service      TEXT    NOT NULL,
			Project      TEXT    NOT NULL,
			NodeId       TEXT    NOT NULL,
			ImageDigest  TEXT    NOT NULL,
			DesiredState INTEGER NOT NULL,
			Relocatable  INTEGER NOT NULL,
			RequiredTags TEXT    NOT NULL,
			ConfigTags   TEXT    NOT NULL,
			ConfigHash   TEXT    NOT NULL,
			UpdatedAt    TEXT    NOT NULL
		);
		CREATE INDEX IF NOT EXISTS ix_deploy_deployment_node ON deploy_deployment (NodeId);
		CREATE UNIQUE INDEX IF NOT EXISTS ux_deploy_deployment_service_node ON deploy_deployment (Service, NodeId);

		CREATE TABLE IF NOT EXISTS deploy_deployment_status (
			NodeId      TEXT    NOT NULL,
			Service     TEXT    NOT NULL,
			ActualState INTEGER NOT NULL,
			ContainerId TEXT,
			ImageDigest TEXT,
			Healthy     INTEGER NOT NULL,
			ReportedAt  TEXT    NOT NULL,
			PRIMARY KEY (NodeId, Service)
		);
		""");

	public override void Down() => Execute.Sql("""
		DROP TABLE IF EXISTS deploy_deployment_status;
		DROP TABLE IF EXISTS deploy_deployment;
		DROP TABLE IF EXISTS deploy_node;
		""");
}
