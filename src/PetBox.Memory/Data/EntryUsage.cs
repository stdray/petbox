using LinqToDB.Mapping;

namespace PetBox.Memory.Data;

// One entry's usage counters (see M007): impressions (surfaced in a recall/search
// answer) vs engagements (opened directly). Pure telemetry — never load-bearing.
[Table("entry_usage")]
public sealed record EntryUsage
{
	// Partition: the owning store (see MemoryEntry.Store) — all of a project's stores share
	// this table, so the counter key is (Store, Key).
	[Column, PrimaryKey(0), NotNull] public string Store { get; init; } = string.Empty;
	[Column, PrimaryKey(1), NotNull] public string Key { get; init; } = string.Empty;
	[Column] public long SurfacedCount { get; init; }
	// The subset of SurfacedCount from a DELIBERATE search (usage:"deliberate") — the honest
	// value signal (machine hook pulls bump SurfacedCount but never this). See M008.
	[Column] public long DeliberateCount { get; init; }
	[Column] public long OpenedCount { get; init; }
	[Column] public DateTime? LastHitAt { get; init; }
}
