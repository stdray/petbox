using LinqToDB.Mapping;

namespace PetBox.Memory.Data;

// One entry's usage counters (see M007): impressions (surfaced in a recall/search
// answer) vs engagements (opened directly). Pure telemetry — never load-bearing.
[Table("entry_usage")]
public sealed record EntryUsage
{
	[Column, PrimaryKey, NotNull] public string Key { get; init; } = string.Empty;
	[Column] public long SurfacedCount { get; init; }
	// The subset of SurfacedCount from a DELIBERATE search (usage:"deliberate") — the honest
	// value signal (machine hook pulls bump SurfacedCount but never this). See M008.
	[Column] public long DeliberateCount { get; init; }
	[Column] public long OpenedCount { get; init; }
	[Column] public DateTime? LastHitAt { get; init; }
}
