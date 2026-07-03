using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Honest usage signal (spec: memoverhaul). SurfacedCount counts EVERY impression —
// including the automatic hook pulls an agent never asked for — which inflates the
// "value" of a fact nobody deliberately reached. DeliberateCount is the subset of
// impressions from a DELIBERATE search (usage:"deliberate"), the signal quarantine
// GC trusts: an entry surfaced only by machine pulls has not proven its worth.
// Existing rows backfill to 0 (we don't know the split retroactively — the honest
// default is "no deliberate value observed yet").
[Migration(8, "Add DeliberateCount to entry_usage (honest usage signal)")]
public sealed class M008_EntryUsageDeliberate : Migration
{
	public override void Up() =>
		Execute.Sql("ALTER TABLE entry_usage ADD COLUMN DeliberateCount INTEGER NOT NULL DEFAULT 0;");

	public override void Down() =>
		Execute.Sql("ALTER TABLE entry_usage DROP COLUMN DeliberateCount;");
}
