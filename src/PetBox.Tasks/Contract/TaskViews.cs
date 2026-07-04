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
	string Status, string Type, string Title, string Body, IReadOnlyList<string> Commits, long Priority, long Version,
	string? Delivery, IReadOnlyList<LinkDto>? Spec, IReadOnlyList<LinkDto>? BlockedBy,
	IReadOnlyList<LinkDto>? LinkedTasks, IReadOnlyList<LinkDto>? Supersedes, IReadOnlyList<string> RenamedFrom, IReadOnlyList<string> Tags,
	string? Url = null);

// A board's active plan nodes (flat list; the tree is the part_of projection via
// ParentNodeId/Depth), plus the board's kind and (work boards) its spec board. This is
// the enrichment core: the Razor board renders it directly and the unified tasks read
// (SearchNodesAsync → tasks_search) composes per-board views from it; the wire budget
// markers live on the unified result, not here.
public sealed record PlanBoardView(
	long CurrentVersion, string Kind, string? SpecBoard, IReadOnlyList<PlanNodeView> Nodes);

// One `[[slug]]` mention resolved to a live project node (node-ref-autolink): the node's
// CURRENT location (Board + Key — the mention may have named a FORMER slug that renamed) plus
// its stable NodeId and Title. A mention that is ambiguous (the slug lives on 2+ boards) or a
// miss is simply absent from the resolution map — the renderer then leaves it literal.
public sealed record NodeRefResolution(string Board, string Key, string NodeId, string Title);

// One node resolved by its stable NodeId alone (cross-board): its owning board + kind,
// the fully-enriched node view, its part_of ancestor chain ordered root→parent (for
// breadcrumbs), and the EXHAUSTIVE relation panel (`Relations`) — every relation kind in
// both directions (children, blocks/blocked-by, implements/linked, idea/spec, issue/tasks,
// supersedes/superseded-by), one group per non-empty kind×direction. Powers the per-node
// detail page, which addresses a node by id, not board.
public sealed record NodeDetailView(
	string Board, string Kind, PlanNodeView Node, IReadOnlyList<NodeCrumb> Ancestors,
	IReadOnlyList<NodeRelationGroup> Relations);

// A lightweight node pointer (no body/links) — used for breadcrumb ancestors.
public sealed record NodeCrumb(string NodeId, string Slug, string Title);

// One relation kind × direction as a labelled bucket of resolved targets (node-relations-panel).
// `Label` is the human name for the direction ("children" = part_of reverse, "blocks" = blocks
// forward, "superseded by" = supersedes reverse, …); each LinkDto in `Links` carries the target's
// live status (so the detail page shows a status chip per row). Emitted only when non-empty, in a
// fixed reading order. Unlike PlanNodeView's typed link fields (spec/blockedBy/…) this is the
// COMPLETE two-way view, so the detail page renders the full graph around a node in one place.
public sealed record NodeRelationGroup(string Label, IReadOnlyList<LinkDto> Links);

// Delta projection of a node (no links/delivery/tags — that's GetAsync). `Body` is the
// compact-echo opt-in (spec echo-compact-by-default): null by default (the serializer
// omits it) so a write-echo carries only key/status/title; a sliced body is filled only
// when the caller passes bodyLen > 0.
public sealed record PlanNodeDelta(
	string Key, string NodeId, string Status, string Type, string Title, string? Body,
	IReadOnlyList<string> Commits, long Priority, long Version, string? Url = null);

// One row the caller could not apply (optimistic-concurrency miss, or a domain-guard
// refusal — then Reason says why), shaped for the wire. On a Stale conflict,
// ChangedFields names THIS node's payload fields that moved past the author's baseline
// (entity-scoped — never other nodes' noise), so the retry is informed, not blind.
public sealed record UpsertConflictView(
	string Key, string Kind, long BaselineVersion, long? ActiveVersion, string? Reason = null,
	IReadOnlyList<string>? ChangedFields = null);

// The tasks_upsert / tasks_delta response. For an upsert it is a pure write-ack: what was
// applied (counts), any Conflicts, and Added/Updated/Removed scoped to THIS call only —
// CurrentVersion is the board-wide cursor for tasks_delta. For a delta it carries the full
// board changes since the caller's cursor (Added/Updated as node projections, Removed keys).
// AutoResolved: keys whose stale baseline was accepted because the node's payload had not
// semantically moved since the author's read (bookkeeping bumps only) — applied, and
// reported so the resolution stays visible.
public sealed record UpsertResultView(
	bool Applied, long CurrentVersion, string Kind, int Inserted, int Closed,
	IReadOnlyList<UpsertConflictView> Conflicts,
	IReadOnlyList<PlanNodeDelta> Added, IReadOnlyList<PlanNodeDelta> Updated, IReadOnlyList<string> Removed,
	IReadOnlyList<string> AutoResolved);

// The raw temporal upsert/delta result plus the board's resolved kind name (a defined
// kind's slug verbatim, else the preset name — lowercase either way), ready for an
// adapter to serialize. The service owns the logic; the adapter owns the wire shape.
public sealed record UpsertOutcome(TemporalUpsertResult<PlanNode> Result, string Kind);

// The workflow surface of one board (tasks_workflow): the resolved kind name plus one
// block per DISTINCT state machine — preset kinds group identical FSMs (feature=bug=
// chore on a work board is one block), definition kinds group by declaration. Transitions
// carry PreconditionArtifact when the definition gates them on a comment artifact.
public sealed record BoardWorkflowView(string Kind, IReadOnlyList<WorkflowBlock> Workflows);

// A tag-projection group: nodes sharing one tag value in a grouped namespace (or the
// "(none)" bucket). `Delivery` is the combined roll-up over every node in the group (spec
// boards only). A node with several tags in the namespace appears in several groups.
// The projection nests by the ORDERED groupBy dimensions: a non-final dimension fills
// `SubGroups` (the next dimension's buckets, scoped to this group's nodes) and leaves
// `NodeKeys` empty; the final dimension is a leaf — `NodeKeys` filled, `SubGroups` empty.
public sealed record TagGroup(
	string Key, string? Delivery, IReadOnlyList<string> NodeKeys, IReadOnlyList<TagGroup> SubGroups);

// A board projected by an ORDERED list of tag namespaces (e.g. [area, concern]): the
// "tree" is a grouping view, not stored hierarchy (spec-flat-tags) — switching it is
// reversible and never touches part_of (tag-grouping-is-projection). Dimension order
// sets nesting; within each level groups are ordered by key, "(none)" last.
public sealed record GroupedBoardView(IReadOnlyList<string> GroupBy, string Kind, IReadOnlyList<TagGroup> Groups);

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

// One unified-read hit: the enriched node view plus its owning board (a read may span
// boards, and PlanNodeView itself doesn't carry the board). Query mode fills Score (the fused
// RRF relevance) and Retriever provenance ("lexical"|"semantic"|"exact"); listing mode leaves
// both null (no relevance ran). MatchedIn = "comment" when this node surfaced because a COMMENT
// under it matched the query (tasks-search-comments) — the hit points at the owner node; null
// when the node itself matched (name/body/tags).
public sealed record TaskSearchHit(string Board, PlanNodeView Node, double? Score = null, string? Retriever = null, string? MatchedIn = null);

// The sort axis of the unified tasks read. Priority = the deterministic listing default
// (priority then key); Relevance = the fused hybrid order, valid ONLY with a query (it is
// the query mode's default); Created/Updated/Title reorder the selected set by node fields.
public enum TaskSortBy
{
	Priority,
	Created,
	Updated,
	Title,
	Relevance,
}

// The filter axis of the unified tasks read — every field is a PREDICATE that narrows the
// pool in both modes (listing and query). `Board` scopes to one board (null = the whole
// project, each row then carries its board). `Under` restricts to a part_of subtree (a
// slug or NodeId; resolved cross-board when Board is null). `Status` keeps only the named
// slugs — naming a TERMINAL slug is an explicit ask and returns those nodes without
// IncludeClosed. `Keys` addresses specific nodes (slug|NodeId mixed, resolved like
// tasks_node_get; terminal nodes included — explicit addressing). `IncludeClosed` widens
// a listing to terminal nodes (query mode searches the open set only).
public sealed record TaskNodeFilter(
	string? Board = null,
	string? Under = null,
	IReadOnlyList<string>? Status = null,
	IReadOnlyList<string>? Keys = null,
	bool IncludeClosed = false,
	// Reverse commit lookup (node-commits-impl): keep only nodes carrying this commit. An
	// EXACT match on a stored sha, or — when the query is >=7 hex chars — a PREFIX match on a
	// stored full sha (a short query finds the long commit). null/empty = no filter.
	string? Commit = null);

// The unified tasks read result (list = search without query): the selected hits in their
// final order, the board context when the read was board-scoped (Kind/SpecBoard/
// CurrentVersion — null on a project-wide read), and retriever provenance (null in listing
// mode; filled in query mode — which retrievers ran and whether the answer is degraded,
// e.g. embedding unavailable so only lexical ran).
public sealed record TaskSearchResult(
	IReadOnlyList<TaskSearchHit> Hits,
	string? Board = null,
	string? Kind = null,
	string? SpecBoard = null,
	long? CurrentVersion = null,
	PetBox.Core.Search.SearchRetrievers? Retrievers = null);

// One board of the methodology quartet as a compact INDEX: a status histogram (`Counts`,
// status slug -> active-node count) plus the board's nodes as header rows (no bodies by
// default). null Name = not provisioned. `Counts` is ALWAYS complete; the node rows are
// subject to the response-wide output budget (spec bounded-result-sets) — when rows were
// cut, `Truncated` is true and `Omitted` says how many rows fell off (null = nothing cut,
// so a board that fits serializes exactly as before).
public sealed record MethodologyBoard(
	string Kind, string? Name, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<PlanNodeHeader> Nodes,
	bool? Truncated = null, int? Omitted = null);

// The methodology quartet as one surface: intake → ideas → spec → work (the pipeline
// order). `Enabled` = all four singleton boards exist. Composes GetAsync per board.
// `Hint` is non-null only when some board's rows were cut by the output budget: a
// human/agent-readable pointer on how to narrow the query (null = complete answer).
public sealed record MethodologyView(bool Enabled, IReadOnlyList<MethodologyBoard> Boards, string? Hint = null);

// Ack of a methodology-definition write: the definition's current revision number (the
// baseline for the next edit), whether this call created a new revision (false = an
// identical resubmit collapsed to a no-op), and how many live nodes the declared
// `migration` rewrote onto the new resolution (0 = nothing needed repair). Conflicts
// throw instead — a singleton document has no partial-batch outcome to report.
public sealed record MethodologyDefAck(long Version, bool Changed, int Migrated = 0);

// The project's active methodology definition plus its revision metadata. A null view
// (from GetMethodologyDefinitionAsync) means the project has no definition and is on the
// built-in MethodologyPresets.
public sealed record MethodologyDefView(MethodologyDefinition Definition, long Version, DateTime Created, DateTime Updated);

// The runtime-derived agent guide to a project's process (tasks_methodology_guide, spec
// artifacts-from-definition): markdown prose + the structured invariants it was derived
// from (the machine-readable form — no markdown re-parsing downstream). `Source` says
// where the effective kinds came from: "presets" (no definition), "definition" (the
// definition overrides every preset kind) or "mixed" (definition kinds + preset fallback).
// DefinitionVersion is the definition's revision when one exists (null on pure presets).
public sealed record MethodologyGuideView(
	string Markdown,
	IReadOnlyList<MethodologyInvariant> Invariants,
	string Source,
	long? DefinitionVersion = null);
