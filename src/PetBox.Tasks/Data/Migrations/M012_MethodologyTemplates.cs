using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Named methodology templates (methodology-template-storage): multi-key temporal (SCD-2)
// documents independent of the live process singleton (methodology_defs Key="methodology")
// and of future instance entities. Key = template slug; payload = MethodologyDefinition
// JSON (camelCase, enums as strings) — same document shape as methodology_defs. Write
// paths never provision boards or rewrite live nodes. Additive: brand-new table only.
// Forward-only.
[Migration(12, "methodology_templates: temporal named methodology template documents (JSON payload)")]
public sealed class M012_MethodologyTemplates : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS methodology_templates (
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Json       TEXT    NOT NULL,
				PrevKey    TEXT,
				ActiveFrom INTEGER NOT NULL,
				ActiveTo   INTEGER,
				Created    TEXT    NOT NULL,
				Updated    TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			""");
		Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_methodology_templates_active_key ON methodology_templates (Key) WHERE ActiveTo IS NULL;");
	}

	public override void Down() { } // forward-only
}
