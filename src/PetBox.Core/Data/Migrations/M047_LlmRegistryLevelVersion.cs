using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// A CAS BASELINE FOR THE LLM REGISTRY (work llm-config-upsert-full-replace-no-cas).
//
// llm_config_upsert replaces a level WHOLE and had no version parameter at all, so two concurrent
// edits could not conflict — the second one simply won, silently, on the production router's own
// configuration. Every other write verb in PetBox (memory_upsert, tasks_upsert,
// tasks_methodology_rules_upsert) takes a WATERMARK baseline and refuses a stale one; this table is
// what lets the LLM registry join that contract.
//
// WHY ITS OWN TABLE, and not a Version column on llm_endpoints/llm_routes: the level is written by
// DELETE-then-INSERT, so a version stored on the rows disappears with them. Emptying a level would
// reset the counter to 0 and a baseline read before the emptying would be accepted again afterwards
// — the exact overwrite the version exists to catch. A row here is created on a level's first write
// and never deleted, so the counter is monotone for the life of the level.
//
// SEEDING. Every level that ALREADY has rows gets Version = 1, not 0: 0 means "this level declares
// nothing yet" (a create), and a caller passing 0 against a level that already serves production
// must be told to re-read, not allowed to overwrite it. INSERT..SELECT has no typed FluentMigrator
// form (Insert.IntoTable takes literal rows only), hence SqliteDdl.Raw.
[Migration(47, "llm_registry_levels: a per-level CAS version for the LLM registry")]
public sealed class M047_LlmRegistryLevelVersion : SqliteMigration
{
	public override void Up()
	{
		Create.Table("llm_registry_levels")
			.WithColumn("Scope").AsString().NotNullable().PrimaryKey()
			.WithColumn("ScopeKey").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedBy").AsInt64().Nullable();

		SqliteDdl.Raw(
			"every level that already declares endpoints or routes must start at version 1 (not 0, " +
			"which means 'declares nothing yet' and would let a caller overwrite a live registry " +
			"without ever reading it) — and INSERT..SELECT has no typed FluentMigrator form",
			"""
			INSERT INTO llm_registry_levels (Scope, ScopeKey, Version, UpdatedAt, UpdatedBy)
			SELECT Scope, ScopeKey, 1, CURRENT_TIMESTAMP, NULL
			FROM (
				SELECT DISTINCT Scope, ScopeKey FROM llm_endpoints
				UNION
				SELECT DISTINCT Scope, ScopeKey FROM llm_routes
			);
			""");
	}

	public override void Down() { } // forward-only
}
