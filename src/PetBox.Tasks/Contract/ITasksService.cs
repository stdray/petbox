using PetBox.Core.Contract;
using PetBox.Core.Models;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Contract;

// The single entry point to the Tasks module for every caller (MCP tools, Razor
// pages, REST). It owns all reads/writes of the task store plus the domain rules
// (spec validation, workflow application, blockers, delivery roll-up, FSM effects),
// so those rules live in exactly one place instead of being re-implemented by each
// surface. Adapters stay thin: parse input, call the service, shape the response.
// A NetArchTest forbids Web tools/pages from reaching ITaskBoardStore / IRelationStore
// / TasksDb directly, so this interface is the only door.
// It also implements the generic uniform-read contract (ISearchService — spec
// uniform-entity-verbs v2): list = search without a query, relevance = a sort option
// available only with a query. SearchNodesAsync is the richer per-family overload (board
// context + URL prefix); the generic SearchAsync is its plain-envelope projection.
public interface ITasksService : ISearchService<TaskSearchHit, TaskNodeFilter, TaskSortBy>
{
	// --- board lifecycle ---

	// Create a board, validating the spec link first. Returns the new metadata row.
	Task<TaskBoardMeta> CreateBoardAsync(string projectKey, string board, string? kind, string? description, string? specBoard, CancellationToken ct = default);
	// Set (or clear, when specBoard is null/empty) a work board's spec board. Returns
	// whether the row changed and the normalized spec board value.
	Task<(bool Set, string? SpecBoard)> SetSpecBoardAsync(string projectKey, string board, string? specBoard, CancellationToken ct = default);
	Task<IReadOnlyList<TaskBoardMeta>> ListBoardsAsync(string projectKey, CancellationToken ct = default);
	Task<bool> DeleteBoardAsync(string projectKey, string board, CancellationToken ct = default);

	// --- methodology quartet (intake+ideas+spec+work as a per-project singleton unit) ---

	// Provision the four singleton boards (intake/ideas/spec/work) if missing and auto-wire
	// work->spec. Idempotent. Returns the quartet surface.
	Task<MethodologyView> EnableMethodologyAsync(string projectKey, CancellationToken ct = default);
	// The quartet as one compact INDEX (intake→ideas→spec→work): header rows (no body by
	// default) + a status histogram per board. `bodyLen`>0 slices the first N body chars into
	// each row; `includeBoards` (kind names) restricts which quartet boards return. Enabled =
	// all four exist (independent of the filter). Node rows share one response-wide output
	// budget (status histograms are always complete); an over-budget board is prefix-cut and
	// flagged Truncated/Omitted, with a narrowing Hint on the view — never silently.
	Task<MethodologyView> GetMethodologyAsync(string projectKey, int bodyLen = 0, string[]? includeBoards = null, string? urlPrefix = null, CancellationToken ct = default);
	// The workspace owning a project, or null if unknown. Adapters use it to assemble per-node
	// UI permalinks (the URL is workspace-scoped but the MCP surface carries only projectKey).
	Task<string?> ResolveWorkspaceAsync(string projectKey, CancellationToken ct = default);
	// --- user-defined methodology definition (LIVE since wave 1.2: a kind the definition
	//     declares resolves types/statuses/transitions from data; any other kind — or a
	//     project without a definition — falls back to the built-in presets) ---

	// Validate and store the project's methodology definition as a new temporal revision.
	// `version` is the baseline the author last saw (0 = "I believe none exists yet");
	// optimistic concurrency: a moved baseline throws, naming the current version so the
	// caller re-reads and rebases. An identical resubmit is a no-op (Changed=false).
	// A CHANGE is checked against live data first (spec primitives-schema-migration):
	// every active node on a board whose kind the old or new definition declares must fit
	// the NEW resolution; `migration` declares per-kind {from,to} type/status repairs for
	// values that don't (applied only where invalid). Any node still incompatible rejects
	// the whole call naming the offenders — nothing is written. When everything is mapped,
	// the definition commits first and the repaired nodes are rewritten as new temporal
	// revisions (a system write — no FSM guards; the mapping IS the sanctioned transition);
	// Migrated counts them.
	Task<MethodologyDefAck> DefineMethodologyAsync(string projectKey, MethodologyDefinition def, long version, IReadOnlyList<MethodologyMigration>? migration = null, CancellationToken ct = default);
	// The project's active methodology definition + its revision metadata, or null when
	// the project has none (it is then on the built-in MethodologyPresets).
	Task<MethodologyDefView?> GetMethodologyDefinitionAsync(string projectKey, CancellationToken ct = default);

	// Close (closed=true) or reopen (closed=false) a board.
	Task<bool> SetClosedAsync(string projectKey, string board, bool closed, CancellationToken ct = default);
	Task<bool> BoardExistsAsync(string projectKey, string board, CancellationToken ct = default);

	// --- nodes ---

	// The active plan nodes of one board (flat slugs + part_of projection) with links and
	// (spec boards) delivery. Kept for the Razor board UI and as the enrichment core the
	// unified SearchNodesAsync composes; the MCP read verb is tasks.search.
	// `status` (slugs, case-insensitive) filters on top of the selection; a terminal status
	// named in the filter returns its nodes even when includeClosed is false (an explicit
	// ask overrides the default hiding); an unknown slug for the board's kind is rejected.
	Task<PlanBoardView> GetAsync(string projectKey, string board, bool includeClosed = false, string? under = null, string? urlPrefix = null, string[]? status = null, CancellationToken ct = default);
	// One node by its stable NodeId alone (cross-board): the enriched node view + its owning
	// board + part_of ancestor chain (root→parent). null when no active node carries the id.
	// Powers the per-node detail page (addresses a node by id, not by board/slug).
	Task<NodeDetailView?> GetNodeAsync(string projectKey, string nodeId, CancellationToken ct = default);
	// One node by its human-readable (board, slug) address — the canonical slug-URL form
	// (node-slug-addressable). Resolves the slug to its NodeId then reuses GetNodeAsync. null
	// when no active node on that board carries the slug.
	Task<NodeDetailView?> GetNodeBySlugAsync(string projectKey, string board, string slug, CancellationToken ct = default);
	// One node of a named board addressed by slug OR NodeId (32-hex = NodeId, mirroring
	// specRef/partOf resolution), returned in FULL — an addressed read, so terminal-status
	// nodes come back too (no includeClosed here). Throws a clear error naming the board
	// when the node doesn't exist (never an empty answer). Composes GetNodeAsync /
	// GetNodeBySlugAsync (the node-detail-page precedent).
	Task<NodeDetailView> GetNodeOnBoardAsync(string projectKey, string board, string node, string? urlPrefix = null, CancellationToken ct = default);
	// Resolve a node reference (slug or 32-hex NodeId) to its stable NodeId — the uniform
	// slug-or-NodeId convention (uniform-node-refs) for surfaces that take a bare node ref
	// (relations, comments). A NodeId-shaped value passes through untouched. A slug resolves
	// over the ACTIVE nodes: on `board` when given (board-unique, so never ambiguous), else
	// across EVERY board in the project — unambiguous resolves; the same slug on 2+ boards
	// is an error naming the boards (pass the NodeId); a miss is a clear error, never null.
	Task<string> ResolveNodeRefAsync(string projectKey, string nodeRef, string? board = null, CancellationToken ct = default);
	// Validate a relation kind against the PROJECT's vocabulary — builtin process kinds
	// (task_spec|issue_task|idea_spec|blocks|part_of|supersedes), builtin neutral kinds
	// (relates_to|depends_on|mirrors — free semantic edges, no FSM effects), and the kinds
	// the project's methodology definition declares (linkKinds). Returns the normalized
	// (lowercased) kind; an unknown kind throws, listing every valid kind for the project.
	// RelationTools calls this before touching the store (the store itself is not
	// project-definition-aware).
	Task<string> ValidateRelationKindAsync(string projectKey, string kind, CancellationToken ct = default);
	// Project a board by an ORDERED list of tag namespaces (e.g. [area, concern]): nodes
	// bucketed by their tag value in each namespace ("(none)" for untagged), nested in
	// dimension order, each group with a delivery roll-up. The projection is a view — it
	// never touches part_of (tag-grouping-is-projection).
	Task<GroupedBoardView> GetGroupedAsync(string projectKey, string board, IReadOnlyList<string> groupBy, CancellationToken ct = default);
	// Declarative temporal upsert of plan nodes (workflow + spec/blocker rules + effects).
	// The result is a pure write-ack (spec sinceversion-contract): the echoed Added/Updated/
	// Removed cover ONLY this call — the patched nodes plus rows revised/closed by the call's
	// own cascade effects (a superseded node obsoleted, an unblocked task, a deleted
	// subtree). The write carries NO cursor parameter and never returns other writers'
	// history; CurrentVersion is the board-wide cursor to feed DeltaAsync (the only
	// delta/catch-up surface).
	Task<UpsertOutcome> UpsertAsync(string projectKey, string board, IReadOnlyList<NodePatch> nodes, CancellationToken ct = default);
	// Nodes added/updated/removed since the cursor (no writes).
	Task<UpsertOutcome> DeltaAsync(string projectKey, string board, long sinceVersion, CancellationToken ct = default);
	// The unified tasks read (spec uniform-entity-verbs v2) behind tasks.search — the one
	// read verb where list = search without a query.
	//   No Query  → deterministic LISTING: the filter's board (or every board — rows then
	//     carry their board), default order priority-then-key, overridable by Sort
	//     (created/updated/title/priority; Relevance is rejected without a query).
	//   With Query → relevance SELECTION over the hybrid machinery (lexical FTS ⊕ semantic
	//     vectors, RRF-fused; open set only): the lexical/filter side is a PREDICATE, the
	//     fused ranking supplies a bounded CANDIDATE POOL of max(3×limit, 50); default
	//     order = fused relevance, an explicit Sort reorders WITHIN the selected set.
	//     Retrievers provenance is filled.
	// Filter fields (board/under/status/keys/includeClosed) narrow the pool in both modes;
	// a terminal status named in Status — and any node addressed via Keys — returns without
	// IncludeClosed (an explicit ask). BodyLen slices row bodies (0 = full); Limit caps rows
	// (0 = unbounded listing / the adapter's query default). Board context (kind/specBoard/
	// currentVersion) is filled when the read is board-scoped.
	Task<TaskSearchResult> SearchNodesAsync(string projectKey, SearchRequest<TaskNodeFilter, TaskSortBy> request, string? urlPrefix = null, CancellationToken ct = default);
	// Ensure the board exists and return its PRESET kind (a definition-declared kind reads
	// as Simple here, like any unknown slug always did). UI pages keep rendering off this;
	// the FSM-aware surface is GetBoardWorkflowAsync.
	Task<BoardKind> ResolveKindAsync(string projectKey, string board, CancellationToken ct = default);
	// The board's workflow surface, DATA-DRIVEN: a kind the project's methodology definition
	// declares resolves from the definition (blocks as declared, transitions carrying
	// preconditionArtifact); any other kind falls back to the built-in presets exactly as
	// before (identical FSMs collapsed into one block). Powers tasks.workflow.
	Task<BoardWorkflowView> GetBoardWorkflowAsync(string projectKey, string board, CancellationToken ct = default);

	// --- UI helpers (board page renders the raw active nodes in its own tree order) ---

	Task<IReadOnlyList<PlanNode>> ListActiveNodesAsync(string projectKey, string board, CancellationToken ct = default);
	// Quick-add from the board UI: drops a node into the `incoming` phase with a
	// generated key, the kind's initial status/type, and a stable NodeId.
	Task QuickAddAsync(string projectKey, string board, string name, string? body, long priority, CancellationToken ct = default);

	// --- system surface (report.issue: report-to-maintainer, not project-scoped) ---

	// File an issue node onto a triage board (auto-created), returning its key.
	Task<string> ReportIssueAsync(string project, string board, string title, string body, CancellationToken ct = default);
}
