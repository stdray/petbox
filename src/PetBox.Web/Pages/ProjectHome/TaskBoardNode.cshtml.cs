using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Web.Pages.Shared;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for ONE node of a task board, addressed by its stable NodeId alone
// (board-independent): /ui/{ws}/{project}/tasks/node/{nodeId}. Shows the full body (no
// truncation) + the discussion thread — the destination the board's abbreviated rows link to.
// Sibling of TaskBoard; named TaskBoardNode (not PlanNode) so it doesn't shadow the
// PetBox.Tasks.Data.PlanNode record. Reads go through ITasksService / ICommentService; the page
// never opens the DB context itself (NetArchTest enforces the door).
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

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		var detail = await _tasks.GetNodeAsync(ProjectKey, NodeId, ct);
		if (detail is null) return NotFound();
		Detail = detail;

		var comments = await _comments.ListForNodeAsync(ProjectKey, detail.Board, NodeId, ct);
		Thread = CommentThread.Flatten(comments);
		return Page();
	}
}
