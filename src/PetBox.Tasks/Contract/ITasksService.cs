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
public interface ITasksService
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
	// Close (closed=true) or reopen (closed=false) a board.
	Task<bool> SetClosedAsync(string projectKey, string board, bool closed, CancellationToken ct = default);
	Task<bool> BoardExistsAsync(string projectKey, string board, CancellationToken ct = default);

	// --- nodes ---

	// The active plan nodes as a 1-to-3 level tree with links and (spec boards) delivery.
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
	// Project a board by an ORDERED list of tag namespaces (e.g. [area, concern]): nodes
	// bucketed by their tag value in each namespace ("(none)" for untagged), nested in
	// dimension order, each group with a delivery roll-up. The projection is a view — it
	// never touches part_of (tag-grouping-is-projection).
	Task<GroupedBoardView> GetGroupedAsync(string projectKey, string board, IReadOnlyList<string> groupBy, CancellationToken ct = default);
	// Declarative temporal upsert of plan nodes (workflow + spec/blocker rules + effects).
	// The echoed Added/Updated/Removed cover ONLY this call by default: the patched nodes
	// plus rows revised/closed by the call's own cascade effects (a superseded node
	// obsoleted, an unblocked task, a deleted subtree). `includeDelta` opts back into the
	// FULL board delta since `sinceVersion` (anyone's edits). CurrentVersion is always the
	// board-wide cursor for DeltaAsync, independent of the echo scope.
	Task<UpsertOutcome> UpsertAsync(string projectKey, string board, IReadOnlyList<NodePatch> nodes, long sinceVersion = 0, bool includeDelta = false, CancellationToken ct = default);
	// Nodes added/updated/removed since the cursor (no writes).
	Task<UpsertOutcome> DeltaAsync(string projectKey, string board, long sinceVersion, CancellationToken ct = default);
	// Hybrid search over the project's active, non-terminal nodes (name/body/tags): lexical
	// FTS5 (token/prefix, so paraphrases hit) fused with semantic vector similarity (RRF),
	// ranked by relevance. `board` scopes to one board (null = all boards). `lexical`/`semantic`
	// (null = both on) toggle each retriever; semantic is silently off when no embedding
	// capability is wired. Returns the fused hits plus retriever provenance.
	Task<TaskSearchResult> SearchAsync(string projectKey, string query, string? board = null, bool? lexical = null, bool? semantic = null, string? urlPrefix = null, CancellationToken ct = default);
	// Ensure the board exists and return its kind (used by the workflow discovery tool).
	Task<BoardKind> ResolveKindAsync(string projectKey, string board, CancellationToken ct = default);

	// --- UI helpers (board page renders the raw active nodes in its own tree order) ---

	Task<IReadOnlyList<PlanNode>> ListActiveNodesAsync(string projectKey, string board, CancellationToken ct = default);
	// Quick-add from the board UI: drops a node into the `incoming` phase with a
	// generated key, the kind's initial status/type, and a stable NodeId.
	Task QuickAddAsync(string projectKey, string board, string name, string? body, long priority, CancellationToken ct = default);

	// --- system surface (report.issue: report-to-maintainer, not project-scoped) ---

	// File an issue node onto a triage board (auto-created), returning its key.
	Task<string> ReportIssueAsync(string project, string board, string title, string body, CancellationToken ct = default);
}
