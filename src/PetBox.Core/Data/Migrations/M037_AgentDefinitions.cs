using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Portable agent-definition store (agent-definition-as-data / definition-selection-key):
// project-scoped named temporal documents (SCD-2). Key = definition slug; payload =
// AgentDefinitionDoc JSON (roles/tier/capabilities/spawn/escalation — no model).
// Partitioned by ProjectKey so version cursors are independent per project. Additive.
// Forward-only.
[Migration(37, "agent_definitions: temporal project-scoped agent definition documents (JSON payload)")]
public sealed class M037_AgentDefinitions : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS agent_definitions (
				ProjectKey TEXT    NOT NULL,
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Json       TEXT    NOT NULL,
				PrevKey    TEXT,
				ActiveFrom INTEGER NOT NULL,
				ActiveTo   INTEGER,
				Created    TEXT    NOT NULL,
				Updated    TEXT    NOT NULL,
				PRIMARY KEY (ProjectKey, Key, Version)
			);
			""");
		Execute.Sql(
			"CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_definitions_active_project_key ON agent_definitions (ProjectKey, Key) WHERE ActiveTo IS NULL;");
		Execute.Sql(
			"CREATE INDEX IF NOT EXISTS ix_agent_definitions_project_active ON agent_definitions (ProjectKey, ActiveTo);");
	}

	public override void Down() { } // forward-only
}
