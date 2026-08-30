using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// A comment under a task node, stored as a temporal (SCD type-2) row — structurally a
// degenerate spec node: a tree (via ParentId) with tags, but NO status/type/priority.
// Identity (Key) is a stable GUID; the active revision is the one whose ActiveTo is null.
// Lives in the per-project tasks file next to plan_nodes (same IScopedDbFactory<TasksDb>),
// owned by a node via the stable NodeId. NOT a TaskNode, so it never enters tasks_search /
// the workflow FSM / delivery roll-ups.
[Table("comments")]
public sealed record CommentRow : TemporalRow
{
	// Partition: which board the owning node lives on. Mirrors TaskNode.Board so the
	// version cursor and key space are per-board. Identity, not payload.
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	// The stable TaskNode.NodeId this comment hangs under (cross-board by id). Identity.
	[Column, NotNull] public string NodeId { get; init; } = string.Empty;
	// Tree edge: the Key of the parent comment, or null for a thread root. A reply's
	// parent must live under the same (Board, NodeId) — enforced in the service.
	[Column, Nullable] public string? ParentId { get; init; }
	[Column, NotNull] public string Author { get; init; } = string.Empty;
	[Column, NotNull] public string Body { get; init; } = string.Empty;

	// comment-slug-and-refs: an OPTIONAL human-readable address for this comment, unique among the
	// ACTIVE comments of the OWNING NODE — not globally, and not per board. Null is the normal
	// state: every comment written before this field existed has none and keeps working everywhere
	// (a comment is still addressed by its Key/GUID, which is what `#comment-{id}` and the
	// resolution map key off first).
	//
	// PAYLOAD, not identity: the Key stays the GUID. That is the whole reason a slug change is NOT
	// a node-style rename — a node's Key IS its slug, so renaming one is a re-key the temporal
	// engine carries through PrevKey lineage; here the identity never moves, so PrevKey has nothing
	// to say about a slug edit and the service refuses one instead (see CommentService.UpsertAsync).
	[Column, Nullable] public string? Slug { get; init; }

	// Only the content (Body/Author/ParentId/Slug) can differ between revisions; Board/NodeId
	// are immutable identity (excluded, like TaskNode excludes Board/NodeId).
	public override bool SamePayload(TemporalRow other) =>
		other is CommentRow c && c.Body == Body && c.Author == Author && c.ParentId == ParentId && c.Slug == Slug;

	public override IReadOnlyList<string> ChangedPayloadFields(TemporalRow other)
	{
		if (other is not CommentRow c) return [];
		var fields = new List<string>();
		if (c.Body != Body) fields.Add("body");
		if (c.Author != Author) fields.Add("author");
		if (c.ParentId != ParentId) fields.Add("parentId");
		if (c.Slug != Slug) fields.Add("slug");
		return fields;
	}

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
