using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Named methodology INSTANCES (methodology-instance-core): multi-key temporal (SCD-2)
// documents that ARE the live process automaton (rules + open/closed). Distinct from
// methodology_defs (legacy project-singleton) and methodology_templates (inert documents).
// Key = instance name (slug); Json = MethodologyDefinition rules; ClosedAt null = open.
// Board membership lives on TaskBoards.MethodologyInstance (Core DB). Forward-only.
[Migration(13, "methodology_instances: temporal named methodology instance documents (rules + closed)")]
public sealed class M013_MethodologyInstances : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS methodology_instances (
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Json       TEXT    NOT NULL,
				ClosedAt   TEXT,
				PrevKey    TEXT,
				ActiveFrom INTEGER NOT NULL,
				ActiveTo   INTEGER,
				Created    TEXT    NOT NULL,
				Updated    TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			""");
		Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_methodology_instances_active_key ON methodology_instances (Key) WHERE ActiveTo IS NULL;");
	}

	public override void Down() { } // forward-only
}
