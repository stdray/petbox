using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// User-defined methodology (wave 1.1 of the engine): one temporal (SCD-2) document per
// project holding the whole MethodologyDefinition as JSON. Key is the fixed singleton
// "methodology", so the table is that document's revision history; the unique active
// index (same pattern as M005/M007) keeps at most one live revision. Additive: a brand-new
// table, nothing else touched. Forward-only.
[Migration(10, "methodology_defs: temporal per-project methodology definition (JSON payload)")]
public sealed class M010_MethodologyDefs : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS methodology_defs (
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
		Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_methodology_defs_active_key ON methodology_defs (Key) WHERE ActiveTo IS NULL;");
	}

	public override void Down() { } // forward-only
}
