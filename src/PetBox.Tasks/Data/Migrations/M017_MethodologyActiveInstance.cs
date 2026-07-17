using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// The project's explicit active-methodology-instance pointer (spec methodology-active-instance):
// a temporal (SCD-2) SINGLETON document in the per-project tasks file, same shape as
// methodology_defs (M010) — Key is the fixed SingletonKey, InstanceName names the pointed-at
// instance. Starts EMPTY on every existing project (no row until tasks_methodology_set_active
// is called), so this migration touches no live data — it only adds a table. Forward-only.
[Migration(17, "methodology_active_instance: temporal singleton pointer to the project's active methodology instance")]
public sealed class M017_MethodologyActiveInstance : SqliteMigration
{
	public override void Up()
	{
		Create.Table("methodology_active_instance")
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("InstanceName").AsString().NotNullable()
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		SqliteDdl.PartialIndex(
			name: "ux_methodology_active_instance_active_key",
			table: "methodology_active_instance",
			columns: ["Key"],
			where: "ActiveTo IS NULL",
			unique: true);
	}

	public override void Down() { } // forward-only
}
