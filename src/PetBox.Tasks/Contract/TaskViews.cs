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
	string? Url = null,
	// The active revision's own Created/Updated (board-sort-impl): free on this read — GetAsync
	// already loads the PlanNode row these come from — so the board's client-side sort toggle
	// (created|updated, alongside priority|title) has real data instead of a NodeId proxy.
	DateTime? CreatedAt = null, DateTime? UpdatedAt = null,
	// The symmetric counterpart of BlockedBy (kanban-blocked-signal review finding): outgoing
	// "blocks" edges FROM this node (the nodes it holds up), vs BlockedBy's incoming edges (the
	// nodes holding it up). Same relation kind, opposite direction — see GetAsync.
	IReadOnlyList<LinkDto>? Blocks = null);

// A board's active plan nodes (flat list; the tree is the part_of projection via
// ParentNodeId/Depth), plus the board's kind and (work boards) its spec board. This is
// the enrichment core: the Razor board renders it directly and the unified tasks read
// (SearchNodesAsync → tasks_search) composes per-board views from it; the wire budget
// markers live on the unified result, not here.
public sealed record PlanBoardView(
	long CurrentVersion, string Kind, string? SpecBoard, IReadOnlyList<PlanNodeView> Nodes);

// board-search-stem-lookup: a SCALAR "has this board changed" probe for a cache/ETag check —
// see ITasksService.GetBoardChangeStampAsync for why it's TWO numbers, not one. Two boards with
// the same (NodeVersion, TagStamp) pair are guaranteed to have identical node payloads AND
// identical active tag sets; either one moving means SOMETHING changed.
public sealed record BoardChangeStamp(long NodeVersion, DateTime? TagStamp);

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
// tasks_node_get; terminal nodes included — explicit addressing). `StatusKind` is the first-class
// visibility facet (spec tasks-search-statuskind-facet): a SET over {open, terminalok,
// terminalcancel} evaluated against the опорный слой (search_meta) in BOTH modes; absence = NEUTRAL
// (everything), never a restricting default. `IncludeClosed` is a DEPRECATED ALIAS onto StatusKind
// (TasksSearchDocs.ResolveStatusKindFacet): includeClosed:true → omit the facet (all);
// includeClosed:false + query → [open, terminalok]; includeClosed:false + listing → [open]. An
// explicit StatusKind WINS over the alias.
public sealed record TaskNodeFilter(
	string? Board = null,
	string? Under = null,
	IReadOnlyList<string>? Status = null,
	IReadOnlyList<string>? Keys = null,
	bool IncludeClosed = false,
	// Reverse commit lookup (node-commits-impl): keep only nodes carrying this commit. An
	// EXACT match on a stored sha, or — when the query is >=7 hex chars — a PREFIX match on a
	// stored full sha (a short query finds the long commit). null/empty = no filter.
	string? Commit = null,
	// The statusKind visibility facet (see the doc above). null = the deprecated IncludeClosed
	// alias decides; a non-empty set is the first-class ask and overrides the alias.
	IReadOnlyList<string>? StatusKind = null);

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

// One board kind a PROVISIONING PRESET (legacy EnableMethodologyAsync) declares, as reported
// back by the enable call itself (methodology-enable-response-scope): `Name` is the board
// that now serves this kind (null only when another board already owns that name and
// nothing could be provisioned), `Created` is true only when THIS call created it (false on
// an idempotent rerun, or when nothing was created), `Counts` is the same cheap status
// histogram MethodologyBoard carries (no node dump), and `Workflow` is the kind's FSM blocks
// (the tasks_workflow shape) so the response is self-contained — no follow-up call needed to
// know how to use what was just provisioned.
public sealed record MethodologyEnableBoard(
	string Kind, string? Name, bool Created, IReadOnlyDictionary<string, int> Counts,
	IReadOnlyList<WorkflowBlock> Workflow);

// Legacy EnableMethodologyAsync response: the preset that was applied, and the board(s) IT
// provisions — NOT the methodology instance list (tasks_methodology_list); a
// non-quartet preset like `classic` has nothing to do with those four boards, so dumping
// them here described the wrong thing).
public sealed record MethodologyEnableResult(string Preset, IReadOnlyList<MethodologyEnableBoard> Boards);

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

// ---- named methodology templates (methodology-template-storage) ----------------------
// Independent of the live process singleton (MethodologyDefView) and of future instance
// entities. Source ∈ stored|builtin|definition:
//   stored     — a row in methodology_templates
//   builtin    — quartet|classic|simple rendered from MethodologyPresets (version 0)
//   definition — dual-read of the legacy singleton def under key "methodology" (compat
//                until instance core lands; def_get still owns the live process read)

// Ack of a template write/delete/snapshot: the template key, its current revision, and
// whether this call created a new revision (false = identical resubmit / delete no-op).
// Conflicts throw — same posture as MethodologyDefAck.
public sealed record MethodologyTemplateAck(string Key, long Version, bool Changed);

// One named template as a full document + envelope. Created/Updated are null for
// builtins (never stored). Version is 0 for builtins.
public sealed record MethodologyTemplateView(
	string Key,
	string Source,
	MethodologyDefinition Definition,
	long Version,
	DateTime? Created,
	DateTime? Updated);

// Compact list row for tasks_methodology_template_list (no full definition body).
public sealed record MethodologyTemplateListItem(
	string Key,
	string Source,
	string Name,
	long Version,
	DateTime? Updated);

// ---- methodology instances (methodology-instance-core) --------------------------------
// A named live process automaton (rules + boards + open/closed). "methodology" without
// qualifier means instance. Process-role singleton (≤1 open board per process-role kind)
// applies INSIDE the instance, not project-wide.

// One board membership row on an instance index (no node bodies).
public sealed record MethodologyInstanceBoard(
	string Name, string Kind, bool Closed, string? SpecBoard = null);

// Compact INDEX of one instance: identity, boards, status, computed summary — no node bodies
// (spec methodology-instance-list-get). Counts = status histogram over open boards' active nodes.
public sealed record MethodologyInstanceView(
	string Name,
	bool Closed,
	long Version,
	DateTime Created,
	DateTime Updated,
	DateTime? ClosedAt,
	// Display name from the stored definition document (may differ from the instance key).
	string DefinitionName,
	IReadOnlyList<string> Kinds,
	IReadOnlyList<MethodologyInstanceBoard> Boards,
	// Active-node status histogram across the instance's open boards (empty when none).
	IReadOnlyDictionary<string, int> Counts,
	// How this instance was created (builtin|template|instance) — informational; may be null
	// for rows written before the field was recorded.
	string? Source = null,
	string? SourceKey = null);

// The project's explicit "active methodology instance" pointer (spec
// methodology-active-instance): controls DEFAULTS only (UI, MCP verbs without an explicit
// instance, tasks_methodology_guide with no `name`) — board membership rules always
// resolve through TaskBoards.MethodologyInstance regardless of what is active here. Name is
// null when no pointer is set (resolution then falls back to the single-open-instance case,
// or an explicit "no default" state — never a silent merge). Version is the CAS baseline
// for tasks_methodology_set_active.
public sealed record MethodologyActiveInstanceView(string? Name, long Version);

// Ack of tasks_methodology_set_active: the resulting pointer (null when cleared), whether
// this call changed the stored value, and the new CAS version.
public sealed record MethodologyActiveInstanceAck(string? Name, bool Changed, long Version);

// Ack of instance create/close: the instance name, whether this call changed state, and
// the boards that were provisioned (create) or closed (close).
public sealed record MethodologyInstanceAck(
	string Name,
	bool Changed,
	bool Closed,
	IReadOnlyList<MethodologyInstanceBoard> Boards,
	long Version = 0);

// Full rules document of one instance (tasks_methodology_rules_get) — the
// baseline for rules_upsert. Closed instances still return their last rules (read-only).
public sealed record MethodologyInstanceRulesView(
	string Name,
	MethodologyDefinition Definition,
	long Version,
	DateTime Created,
	DateTime Updated,
	bool Closed);

// Ack of instance rules edit: version cursor, whether a new revision was written, and
// how many live nodes the declared `migration` rewrote (0 = none needed). Conflicts /
// unmapped stranded values throw — nothing partially applied.
public sealed record MethodologyInstanceRulesAck(
	string Name,
	long Version,
	bool Changed,
	int Migrated = 0);

// MethodologyGuideView moved to PetBox.Tasks.Engine (methodology-engine-extraction, slice
// 2) — see src/PetBox.Tasks.Engine/Contract/MethodologyGuideView.cs. Namespace unchanged
// (PetBox.Tasks.Contract); PetBox.Tasks references Engine, so this using still resolves it.
