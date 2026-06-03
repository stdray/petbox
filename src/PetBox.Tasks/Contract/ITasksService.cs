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
	// The quartet as one surface (intake→ideas→spec→work). Enabled = all four exist.
	Task<MethodologyView> GetMethodologyAsync(string projectKey, CancellationToken ct = default);
	// Close (closed=true) or reopen (closed=false) a board.
	Task<bool> SetClosedAsync(string projectKey, string board, bool closed, CancellationToken ct = default);
	Task<bool> BoardExistsAsync(string projectKey, string board, CancellationToken ct = default);

	// --- nodes ---

	// The active plan nodes as a 1-to-3 level tree with links and (spec boards) delivery.
	Task<PlanBoardView> GetAsync(string projectKey, string board, bool includeClosed = false, string? under = null, CancellationToken ct = default);
	// Project a board by a tag namespace (area|concern): nodes bucketed by their tag value
	// in that namespace ("(none)" for untagged), each group with a delivery roll-up.
	Task<GroupedBoardView> GetGroupedAsync(string projectKey, string board, string groupBy, CancellationToken ct = default);
	// Declarative temporal upsert of plan nodes (workflow + spec/blocker rules + effects).
	Task<UpsertOutcome> UpsertAsync(string projectKey, string board, IReadOnlyList<NodePatch> nodes, long sinceVersion = 0, CancellationToken ct = default);
	// Nodes added/updated/removed since the cursor (no writes).
	Task<UpsertOutcome> DeltaAsync(string projectKey, string board, long sinceVersion, CancellationToken ct = default);
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
