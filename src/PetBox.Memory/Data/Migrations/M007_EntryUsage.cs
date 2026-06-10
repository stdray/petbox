using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Usage telemetry per entry (spec: memory-usage-observability): how often an entry
// SURFACED in a recall/search answer vs was OPENED directly (memory.get) — the two
// signals behind procedural-lift graduation/cleanup decisions. Counters are telemetry,
// not state: losing rows only loses statistics.
[Migration(7, "Per-entry usage counters (entry_usage)")]
public sealed class M007_EntryUsage : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS entry_usage (
				Key TEXT NOT NULL PRIMARY KEY,
				SurfacedCount INTEGER NOT NULL DEFAULT 0,
				OpenedCount INTEGER NOT NULL DEFAULT 0,
				LastHitAt TEXT NULL
			);
			""");
	}

	public override void Down()
	{
		Execute.Sql("DROP TABLE IF EXISTS entry_usage;");
	}
}
