using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// User-defined methodology (wave 1.1 of the engine): one temporal (SCD-2) document per project
// holding the whole MethodologyDefinition as JSON. Key is the fixed singleton "methodology", so
// the table is that document's revision history; the unique ACTIVE index (same pattern as
// M005/M007) keeps at most one live revision. Additive: a brand-new table, nothing else touched.
// Forward-only.
//
// Typed DDL; the partial unique index — the temporal invariant — has no typed form and goes
// through the named, guarded SqliteDdl.PartialIndex.
[Migration(10, "methodology_defs: temporal per-project methodology definition (JSON payload)")]
public sealed class M010_MethodologyDefs : SqliteMigration
{
	public override void Up()
	{
		Create.Table("methodology_defs")
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Json").AsString().NotNullable()
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		SqliteDdl.PartialIndex(
			name: "ux_methodology_defs_active_key",
			table: "methodology_defs",
			columns: ["Key"],
			where: "ActiveTo IS NULL",
			unique: true);
	}

	public override void Down() { } // forward-only
}
