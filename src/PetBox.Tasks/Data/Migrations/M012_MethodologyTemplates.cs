using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Named methodology templates (methodology-template-storage): multi-key temporal (SCD-2) documents
// independent of the live process singleton (methodology_defs Key="methodology") and of future
// instance entities. Key = template slug; payload = MethodologyDefinition JSON (camelCase, enums as
// strings) — same document shape as methodology_defs. Write paths never provision boards or rewrite
// live nodes. Additive: brand-new table only. Forward-only.
//
// Typed DDL; the partial unique index (at most one ACTIVE revision per template) has no typed form
// and goes through the named, guarded SqliteDdl.PartialIndex.
[Migration(12, "methodology_templates: temporal named methodology template documents (JSON payload)")]
public sealed class M012_MethodologyTemplates : SqliteMigration
{
	public override void Up()
	{
		Create.Table("methodology_templates")
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Json").AsString().NotNullable()
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		SqliteDdl.PartialIndex(
			name: "ux_methodology_templates_active_key",
			table: "methodology_templates",
			columns: ["Key"],
			where: "ActiveTo IS NULL",
			unique: true);
	}

	public override void Down() { } // forward-only
}
