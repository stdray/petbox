using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.Shared;

namespace PetBox.Web.Pages.ProjectHome;

// Detail for ONE node of a task board, addressed by its stable NodeId alone (board-independent):
// /ui/{ws}/{project}/tasks/node/{nodeId}. Shows the full body (no truncation) + the discussion
// thread — the destination the board's abbreviated rows link to — and lets a human EDIT the node
// (status, title, body) in place. Sibling of TaskBoard; named TaskBoardNode (not PlanNode) so it
// doesn't shadow the PetBox.Tasks.Data.PlanNode record. Every read AND write goes through
// ITasksService / ICommentService — the page never opens the DB context itself, and the edit
// handlers route through UpsertAsync so a UI edit hits the SAME guards (FSM, ideaRef, concurrency)
// as the MCP path (spec edit-respects-guards). NetArchTest enforces the door.
[Authorize]
public sealed class TaskBoardNodeModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;
	readonly ICommentService _comments;

	public TaskBoardNodeModel(FeatureFlags features, ITasksService tasks, ICommentService comments)
	{
		_features = features;
		_tasks = tasks;
		_comments = comments;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "nodeId")]
	public string NodeId { get; set; } = string.Empty;

	// The resolved node (board + enriched view + breadcrumb ancestors). Set on a 200.
	public NodeDetailView Detail { get; private set; } = default!;
	// The node's discussion thread, DFS-flattened (shared shape with the board page).
	public IReadOnlyList<CommentLine> Thread { get; private set; } = [];
	// Legal next statuses from the node's current status (edit-status). The status form offers
	// only these; an empty list means no transition is available (e.g. a terminal status).
	public IReadOnlyList<string> NextStatuses { get; private set; } = [];
	// Set when a write was rejected (a guard violation or an optimistic-concurrency conflict):
	// the edit is re-rendered with the message inline rather than silently dropped.
	public string? Error { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await LoadAsync(ct);
	}

	// edit-status: move the node to a new status. The select offers only NextStatuses, but the
	// service re-validates the transition (and runs the FSM effects) — the UI can't bypass it.
	public async Task<IActionResult> OnPostStatusAsync(string status, long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await ApplyAsync(p => p with { Status = status }, version, ct);
	}

	// edit-title-body: replace the node's title and body (markdown). An empty field clears it.
	public async Task<IActionResult> OnPostEditAsync(string? title, string? body, long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await ApplyAsync(p => p with { Title = title ?? string.Empty, Body = body ?? string.Empty }, version, ct);
	}

	// Resolve the node by its id (so the caller can't retarget the board/key), apply the patch
	// through the service door, and PRG on success. A guard rejection (ArgumentException /
	// InvalidOperationException) or an optimistic-concurrency conflict re-renders the page with
	// the error surfaced inline — the edit is never silently bypassed (edit-respects-guards).
	async Task<IActionResult> ApplyAsync(Func<NodePatch, NodePatch> mutate, long version, CancellationToken ct)
	{
		var detail = await _tasks.GetNodeAsync(ProjectKey, NodeId, ct);
		if (detail is null) return NotFound();

		var patch = mutate(new NodePatch { Key = detail.Node.Key, Version = version });
		try
		{
			var outcome = await _tasks.UpsertAsync(ProjectKey, detail.Board, [patch], ct: ct);
			if (outcome.Result.Conflicts.Count > 0)
				return await LoadAsync(ct, "Узел изменился с момента открытия страницы — обновите её и повторите правку.");
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			return await LoadAsync(ct, ex.Message);
		}

		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, nodeId = NodeId });
	}

	// Shared load for the GET and the error re-render: resolve the node, compute the legal next
	// statuses, and flatten its comment thread.
	async Task<IActionResult> LoadAsync(CancellationToken ct, string? error = null)
	{
		var detail = await _tasks.GetNodeAsync(ProjectKey, NodeId, ct);
		if (detail is null) return NotFound();
		Detail = detail;
		Error = error;

		var kind = WorkflowCatalog.ParseKind(detail.Kind);
		var type = detail.Node.Type.Length == 0 ? null : detail.Node.Type;
		NextStatuses = WorkflowCatalog.For(kind, type)?.NextFrom(detail.Node.Status) ?? [];

		var comments = await _comments.ListForNodeAsync(ProjectKey, detail.Board, NodeId, ct);
		Thread = CommentThread.Flatten(comments);
		return Page();
	}
}
