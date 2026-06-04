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
	IReadOnlyList<LinkDto>? LinkedTasks, IReadOnlyList<LinkDto>? Supersedes, IReadOnlyList<string> RenamedFrom, IReadOnlyList<string> Tags);

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
	string? CommitRef, long Priority, long Version);

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

// One board of the methodology quartet with its active nodes (null Name = not provisioned).
public sealed record MethodologyBoard(string Kind, string? Name, IReadOnlyList<PlanNodeView> Nodes);

// The methodology quartet as one surface: intake → ideas → spec → work (the pipeline
// order). `Enabled` = all four singleton boards exist. Composes GetAsync per board.
public sealed record MethodologyView(bool Enabled, IReadOnlyList<MethodologyBoard> Boards);
