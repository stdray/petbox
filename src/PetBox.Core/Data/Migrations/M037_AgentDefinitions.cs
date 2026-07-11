using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Portable agent-definition store (agent-definition-as-data / definition-selection-key):
// project-scoped named temporal documents (SCD-2). Key = definition slug; payload =
// AgentDefinitionDoc JSON (roles/tier/capabilities/spawn/escalation — no model).
// Partitioned by ProjectKey so version cursors are independent per project. Additive.
// Forward-only.
//
// The table and the plain lookup index are typed FluentMigrator DDL. The one thing the typed API
// cannot express is the PARTIAL unique index — "at most ONE active revision (ActiveTo IS NULL)
// per (ProjectKey, Key)", the invariant the whole temporal model rests on — so it goes through
// SqliteDdl.PartialIndex: named at the call site, and guarded to FAIL on a non-SQLite engine
// rather than silently degrade into a TOTAL unique index (which would forbid history outright).
//
// This migration used to carry `IF NOT EXISTS` on all three objects. That is gone: a migration
// runs exactly once, gated by VersionInfo, so a tolerant CREATE never protected anything — it
// only stood ready to swallow a schema divergence. Every column here was already explicitly
// NOT NULL / nullable in the old raw DDL, so the typed rewrite is byte-identical in schema
// (see tests/PetBox.Tests/Data/Schema/core.schema.txt).
[Migration(37, "agent_definitions: temporal project-scoped agent definition documents (JSON payload)")]
public sealed class M037_AgentDefinitions : SqliteMigration
{
	public override void Up()
	{
		Create.Table("agent_definitions")
			.WithColumn("ProjectKey").AsString().NotNullable().PrimaryKey()
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Json").AsString().NotNullable()
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		SqliteDdl.PartialIndex(
			name: "ux_agent_definitions_active_project_key",
			table: "agent_definitions",
			columns: ["ProjectKey", "Key"],
			where: "ActiveTo IS NULL",
			unique: true);

		Create.Index("ix_agent_definitions_project_active").OnTable("agent_definitions")
			.OnColumn("ProjectKey").Ascending()
			.OnColumn("ActiveTo").Ascending();
	}

	public override void Down() { } // forward-only
}
