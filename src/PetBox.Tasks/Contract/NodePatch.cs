namespace PetBox.Tasks.Contract;

// A single node as submitted to ITasksService.UpsertAsync. The adapter (MCP/REST/UI)
// owns input parsing and addressing — it resolves the node's path to a canonical Key
// (and PrevKey for a rename) before handing it here. Read-merge semantics live in the
// service: a string field that is null was OMITTED (inherit the prior active row),
// while a non-null value (including "") is an explicit set/clear. Priority null = omit.
// Commits is an SCD-2 association like Tags: null = don't touch, [] = clear, a list REPLACES.
public sealed record NodePatch
{
	// Flat board-unique slug, already validated/normalized by the adapter (no '/').
	public required string Key { get; init; }
	// Slug of the node being renamed from, or null.
	public string? PrevKey { get; init; }

	// Vertical decomposition: the parent this node is part_of. A parent slug (resolved on
	// this board) or a NodeId. null = OMIT (leave the node's parent edge as-is); "" =
	// DETACH (make it a root). Replaces the old l1/l2/l3 path nesting.
	public string? PartOf { get; init; }

	// This node SUPERSEDES (replaces, obsoletes) another — a slug on this board or a NodeId.
	// Records a supersedes edge and moves the superseded node to its kind's terminal-cancel
	// status (deprecated/rejected/cancelled). null = none.
	public string? Supersedes { get; init; }
	// Baseline version the author last saw (0 = new) — drives optimistic concurrency.
	public long Version { get; init; }

	public string? Status { get; init; }
	public string? Type { get; init; }
	public string? Title { get; init; }
	public string? Body { get; init; }
	// First-class reason for a RequiresReason status transition on THIS call. Not merged
	// into the node body — when the transition demands a reason the service requires a
	// non-empty value here and persists it as an `artifact:reason` comment.
	public string? Reason { get; init; }
	public long? Priority { get; init; }

	// Attached commit SHAs (node-commits-impl). null = OMIT (leave the node's commits as-is);
	// a non-null list (incl. empty) REPLACES the node's full commit set — same semantics as
	// Tags. Values are normalized (trim, lowercase, dedupe, drop empties) and validated (hex,
	// 7..40 chars) by the service.
	public IReadOnlyList<string>? Commits { get; init; }

	// Per-node links addressed by relation-kind slug (spec methodology-link-kinds-declared): kind
	// slug -> the refs (slug|NodeId) this node links via that kind. The generic write door that
	// replaced the specRef/ideaRef sugar — a work task carries `task_spec`, a spec write carries
	// `idea_spec`. The target end and edge orientation come from the kind's declared Direction.
	// null/empty = no links given.
	public IReadOnlyDictionary<string, IReadOnlyList<string>>? Links { get; init; }

	// blockedBy → a NodeId|slug that blocks this task (builtin `blocks`, direction-less structural
	// edge). Kept as sugar; also expressible as links.blocks (not both). Null/empty = no link.
	public string? BlockedBy { get; init; }

	// Enforced tags ("namespace:value", namespaces area|concern). null = OMIT (leave the
	// node's tags as-is); a non-null list (incl. empty) REPLACES the node's full tag set.
	public IReadOnlyList<string>? Tags { get; init; }

	// Soft-delete marker: only Key + Version are honored (Version 0 = delete the active row
	// regardless; non-zero = optimistic baseline → Stale conflict on mismatch). The row is
	// temporal-closed (history kept), its edges and tags are closed, and it leaves the search
	// index. Cannot combine with PrevKey. A node with active part_of children is refused.
	public bool Deleted { get; init; }
}
