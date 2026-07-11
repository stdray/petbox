using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Usage telemetry per entry (spec: memory-usage-observability): how often an entry
// SURFACED in a recall/search answer vs was OPENED directly (memory_get) — the two
// signals behind procedural-lift graduation/cleanup decisions. Counters are telemetry,
// not state: losing rows only loses statistics.
//
// M009 later repartitions this table by Store (rebuild, PK becomes (Store, Key)).
[Migration(7, "Per-entry usage counters (entry_usage)")]
public sealed class M007_EntryUsage : Migration
{
	public override void Up() =>
		Create.Table("entry_usage")
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("SurfacedCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("OpenedCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("LastHitAt").AsString().Nullable();

	// No `IF EXISTS`: Up() created entry_usage.
	public override void Down() => Delete.Table("entry_usage");
}
