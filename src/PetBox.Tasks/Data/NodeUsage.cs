using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// One node's usage counters (see M022): impressions (surfaced in a search/listing answer) vs
// engagements (opened directly via tasks_node_get). Pure telemetry — never load-bearing.
[Table("node_usage")]
public sealed record NodeUsage
{
	// Partition: the owning board (see TaskNode.Board) — all of a project's boards share this
	// table, so the counter key is (Board, NodeId).
	[Column, PrimaryKey(0), NotNull] public string Board { get; init; } = string.Empty;
	// The node's STABLE id, not its slug Key: a node survives a re-key (tasks_upsert `prevKey`)
	// and its measured history must survive it too. A counter keyed by slug would silently reset
	// to zero on a rename and read as a brand-new, never-surfaced node.
	[Column, PrimaryKey(1), NotNull] public string NodeId { get; init; } = string.Empty;
	[Column] public long SurfacedCount { get; init; }
	// The subset of SurfacedCount from a DELIBERATE read (usageSource:"deliberate") — the honest
	// value signal; machine/context pulls bump SurfacedCount but never this. In the FIRST
	// migration, deliberately: memory shipped its equivalent two months late and every number in
	// between was a blend of intent and automation.
	[Column] public long DeliberateCount { get; init; }
	[Column] public long OpenedCount { get; init; }
	[Column] public DateTime? LastHitAt { get; init; }
}
