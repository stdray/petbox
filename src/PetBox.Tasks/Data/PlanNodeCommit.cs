using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// SCD-2 edge attaching a commit SHA to a node's stable NodeId (node-commits-impl).
// Mirrors NodeTag exactly: a node's commits are a temporal set bound to its stable
// NodeId (so they survive renames), active while ValidTo is null; removing a commit
// soft-closes the row (history kept). A feature is usually SEVERAL commits, so a node
// may carry many active rows. Board is a denormalized mirror of the node's partition so
// a board-scoped read needs no join. `Sha` is normalized (lowercased hex, 7..40 chars).
[Table("plan_node_commits")]
public sealed record PlanNodeCommit
{
	[Column, NotNull] public string NodeId { get; init; } = string.Empty;
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	[Column, NotNull] public string Sha { get; init; } = string.Empty;
	[Column, NotNull] public DateTime ValidFrom { get; init; }
	[Column, Nullable] public DateTime? ValidTo { get; init; }
}
