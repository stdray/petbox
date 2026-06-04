using PetBox.Core.Data.Temporal;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Contract;

// Public response shapes for the Tasks service. Adapters serialize these (the MCP
// serializer camelCases them); the service owns how they are computed.

// Resolves a link target to its location (board + flat slug); a `Status` of "missing"
// means the target no longer exists.
public sealed record LinkDto(string NodeId, string? Board, string? Slug, string? Title, string Status);

// A plan node enriched with its links, enforced tags, and (on a spec board) computed
// delivery roll-up. Hierarchy is the part_of edge: ParentNodeId/ParentSlug name the
// parent (null = root); Depth is its computed distance from a root.
public sealed record PlanNodeView(
	string Key, string NodeId, string? ParentNodeId, string? ParentSlug, int Depth,
	string Status, string Type, string Title, string Body, string? CommitRef, long Priority, long Version,
	string? Delivery, IReadOnlyList<LinkDto>? Spec, IReadOnlyList<LinkDto>? BlockedBy,
	IReadOnlyList<LinkDto>? LinkedTasks, IReadOnlyList<LinkDto>? Supersedes, IReadOnlyList<string> RenamedFrom, IReadOnlyList<string> Tags,
	string? Url = null);

// A board's active plan nodes (flat list; the tree is the part_of projection via
// ParentNodeId/Depth), plus the board's kind and (work boards) its spec board.
public sealed record PlanBoardView(long CurrentVersion, string Kind, string? SpecBoard, IReadOnlyList<PlanNodeView> Nodes);

// One node resolved by its stable NodeId alone (cross-board): its owning board + kind,
// the fully-enriched node view, and its part_of ancestor chain ordered root→parent (for
// breadcrumbs). Powers the per-node detail page, which addresses a node by id, not board.
public sealed record NodeDetailView(string Board, string Kind, PlanNodeView Node, IReadOnlyList<NodeCrumb> Ancestors);

// A lightweight node pointer (no body/links) — used for breadcrumb ancestors.
public sealed record NodeCrumb(string NodeId, string Slug, string Title);

// Delta projection of a node (no links/delivery/tags — that's GetAsync).
public sealed record PlanNodeDelta(
	string Key, string NodeId, string Status, string Type, string Title, string Body,
	string? CommitRef, long Priority, long Version, string? Url = null);

// One row the caller could not apply (optimistic-concurrency miss), shaped for the wire.
public sealed record UpsertConflictView(string Key, string Kind, long BaselineVersion, long? ActiveVersion);

// The tasks.upsert / tasks.delta response: what was applied (counts) plus the delta since the
// caller's cursor (Added/Updated as node projections, Removed as keys) and any Conflicts. The
// delta IS the fresh state since `sinceVersion` — the caller advances its cursor and merges.
public sealed record UpsertResultView(
	bool Applied, long CurrentVersion, string Kind, int Inserted, int Closed,
	IReadOnlyList<UpsertConflictView> Conflicts,
	IReadOnlyList<PlanNodeDelta> Added, IReadOnlyList<PlanNodeDelta> Updated, IReadOnlyList<string> Removed);

// The raw temporal upsert/delta result plus the board kind, ready for an adapter to
// serialize. The service owns the logic; the adapter owns the wire shape.
public sealed record UpsertOutcome(TemporalUpsertResult<PlanNode> Result, BoardKind Kind);

// A tag-projection group: nodes sharing one tag value in the grouped namespace (or the
// "(none)" bucket). `Delivery` is the combined roll-up over the group's nodes (spec
// boards only). A node with several tags in the namespace appears in several groups.
public sealed record TagGroup(string Key, string? Delivery, IReadOnlyList<string> NodeKeys);

// A board projected by a tag namespace (area|concern): the "tree" is a grouping view, not
// stored hierarchy (spec-flat-tags). Groups are ordered by key, "(none)" last.
public sealed record GroupedBoardView(string GroupBy, string Kind, IReadOnlyList<TagGroup> Groups);

// A compact INDEX projection of a plan node for the methodology surface: identity,
// part_of navigation, status/type/title, tags (always), links and the computed delivery
// roll-up — but NO `Body` by default (sliced to the requested length, else null). The full
// untruncated body is fetched per board/subtree via GetAsync. This is the index altitude
// (spec read-index-altitude): orientation without paying for every node's body.
public sealed record PlanNodeHeader(
	string Key, string NodeId, string? ParentNodeId, string? ParentSlug, int Depth,
	string Status, string Type, string Title, long Priority,
	string? Body, string? Delivery,
	IReadOnlyList<LinkDto>? Spec, IReadOnlyList<LinkDto>? BlockedBy,
	IReadOnlyList<LinkDto>? LinkedTasks, IReadOnlyList<LinkDto>? Supersedes,
	IReadOnlyList<string> Tags, string? Url = null);

// One board of the methodology quartet as a compact INDEX: a status histogram (`Counts`,
// status slug -> active-node count) plus the board's nodes as header rows (no bodies by
// default). null Name = not provisioned.
public sealed record MethodologyBoard(string Kind, string? Name, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<PlanNodeHeader> Nodes);

// The methodology quartet as one surface: intake → ideas → spec → work (the pipeline
// order). `Enabled` = all four singleton boards exist. Composes GetAsync per board.
public sealed record MethodologyView(bool Enabled, IReadOnlyList<MethodologyBoard> Boards);
