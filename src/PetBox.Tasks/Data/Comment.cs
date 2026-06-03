using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// A comment under a plan node, stored as a temporal (SCD type-2) row — structurally a
// degenerate spec node: a tree (via ParentId) with tags, but NO status/type/priority.
// Identity (Key) is a stable GUID; the active revision is the one whose ActiveTo is null.
// Lives in the per-project tasks file next to plan_nodes (same IScopedDbFactory<TasksDb>),
// owned by a node via the stable NodeId. NOT a PlanNode, so it never enters tasks.get /
// the workflow FSM / delivery roll-ups.
[Table("comments")]
public sealed record CommentRow : TemporalRow
{
	// Partition: which board the owning node lives on. Mirrors PlanNode.Board so the
	// version cursor and key space are per-board. Identity, not payload.
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	// The stable PlanNode.NodeId this comment hangs under (cross-board by id). Identity.
	[Column, NotNull] public string NodeId { get; init; } = string.Empty;
	// Tree edge: the Key of the parent comment, or null for a thread root. A reply's
	// parent must live under the same (Board, NodeId) — enforced in the service.
	[Column, Nullable] public string? ParentId { get; init; }
	[Column, NotNull] public string Author { get; init; } = string.Empty;
	[Column, NotNull] public string Body { get; init; } = string.Empty;

	// Only the content (Body/Author/ParentId) can differ between revisions; Board/NodeId
	// are immutable identity (excluded, like PlanNode excludes Board/NodeId).
	public override bool SamePayload(TemporalRow other) =>
		other is CommentRow c && c.Body == Body && c.Author == Author && c.ParentId == ParentId;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}

// SCD-2 edge attaching an OPEN tag to a comment's Key (active while ValidTo is null),
// mirroring node_tag but WITHOUT the controlled vocabulary — any "namespace:value" (or
// bare string) is allowed. Convention: `artifact:<slug>` marks a key deliberation artifact
// (e.g. a spec-update plan). Board is denormalized so a whole board's comment tags load
// without a join.
[Table("comment_tag")]
public sealed record CommentTag
{
	[Column, NotNull] public string CommentId { get; init; } = string.Empty;
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	[Column, NotNull] public string Tag { get; init; } = string.Empty;
	[Column, NotNull] public DateTime ValidFrom { get; init; }
	[Column, Nullable] public DateTime? ValidTo { get; init; }
}
