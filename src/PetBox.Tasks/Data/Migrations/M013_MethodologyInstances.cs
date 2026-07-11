using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Named methodology INSTANCES (methodology-instance-core): multi-key temporal (SCD-2) documents
// that ARE the live process automaton (rules + open/closed). Distinct from methodology_defs (legacy
// project-singleton) and methodology_templates (inert documents). Key = instance name (slug);
// Json = MethodologyDefinition rules; ClosedAt null = open. Board membership lives on
// TaskBoards.MethodologyInstance (Core DB). Forward-only.
//
// Typed DDL; the partial unique index (at most one ACTIVE revision per instance) has no typed form
// and goes through the named, guarded SqliteDdl.PartialIndex.
[Migration(13, "methodology_instances: temporal named methodology instance documents (rules + closed)")]
public sealed class M013_MethodologyInstances : SqliteMigration
{
	public override void Up()
	{
		Create.Table("methodology_instances")
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Json").AsString().NotNullable()
			.WithColumn("ClosedAt").AsString().Nullable()
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		SqliteDdl.PartialIndex(
			name: "ux_methodology_instances_active_key",
			table: "methodology_instances",
			columns: ["Key"],
			where: "ActiveTo IS NULL",
			unique: true);
	}

	public override void Down() { } // forward-only
}
