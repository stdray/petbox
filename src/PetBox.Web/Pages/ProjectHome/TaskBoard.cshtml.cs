using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one task board (/ui/{ws}/{project}/tasks/{board}). Shows
// the currently-active plan nodes (ActiveTo == null) in plan-tree order. Reads and
// the quick-add write both go through ITasksService — the page never opens the DB
// context itself, so quick-add gets the same NodeId/status handling the MCP path does.
[Authorize]
public sealed class TaskBoardModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;

	public TaskBoardModel(FeatureFlags features, ITasksService tasks)
	{
		_features = features;
		_tasks = tasks;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "board")]
	public string Board { get; set; } = string.Empty;

	// Nodes in plan-tree (DFS) render order; ClosedWithActiveDescendant holds the
	// NodeIds of Done/Cancelled nodes that must stay visible under "active only"
	// because a descendant is still open (else the children would orphan).
	public IReadOnlyList<PlanNodeView> Nodes { get; private set; } = [];
	public IReadOnlySet<string> ClosedWithActiveDescendant { get; private set; }
		= new HashSet<string>(StringComparer.Ordinal);

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		// includeClosed: we render closed nodes too (the "active only" toggle hides them
		// client-side); GetAsync supplies each node's part_of parent + depth.
		var view = await _tasks.GetAsync(ProjectKey, Board, includeClosed: true, ct: ct);
		Nodes = OrderHierarchically([.. view.Nodes], out var keepVisible);
		ClosedWithActiveDescendant = keepVisible;
		return Page();
	}

	// Render order is the plan tree itself — DFS by part_of parent, siblings ordered by
	// Priority then Key. A flat priority sort let a low-priority child of an early branch
	// visually drift past a later one (finding D11). DFS keeps every node under its parent.
	static List<PlanNodeView> OrderHierarchically(
		List<PlanNodeView> nodes, out IReadOnlySet<string> closedWithActiveDescendant)
	{
		var byId = new Dictionary<string, PlanNodeView>(StringComparer.Ordinal);
		foreach (var n in nodes) byId[n.NodeId] = n;

		// A node is a root when it has no part_of parent, or its parent isn't on this board.
		static string? ParentOf(PlanNodeView n) => n.ParentNodeId;

		var childMap = nodes
			.Where(n => ParentOf(n) is { } pid && byId.ContainsKey(pid))
			.GroupBy(n => ParentOf(n)!)
			.ToDictionary(
				g => g.Key,
				g => (IReadOnlyList<PlanNodeView>)g
					.OrderBy(n => n.Priority)
					.ThenBy(n => n.Key, StringComparer.Ordinal)
					.ToList(),
				StringComparer.Ordinal);

		var roots = nodes
			.Where(n => { var pk = ParentOf(n); return pk is null || !byId.ContainsKey(pk); })
			.OrderBy(n => n.Priority)
			.ThenBy(n => n.Key, StringComparer.Ordinal)
			.ToList();

		var ordered = new List<PlanNodeView>(nodes.Count);
		var closedKeep = new HashSet<string>(StringComparer.Ordinal);

		// Returns whether the subtree holds a non-closed node, so a closed parent of open
		// work stays visible. Guarded against part_of cycles via the visited set.
		var visited = new HashSet<string>(StringComparer.Ordinal);
		bool Emit(PlanNodeView node)
		{
			if (!visited.Add(node.NodeId)) return false;
			ordered.Add(node);
			var closed = WorkflowCatalog.IsTerminalSlug(node.Status);
			var hasActiveDescendant = false;
			if (childMap.TryGetValue(node.NodeId, out var kids))
				foreach (var kid in kids)
					hasActiveDescendant |= Emit(kid);
			if (closed && hasActiveDescendant) closedKeep.Add(node.NodeId);
			return !closed || hasActiveDescendant;
		}

		foreach (var r in roots) Emit(r);
		closedWithActiveDescendant = closedKeep;
		return ordered;
	}

	// Quick-add from the board UI: drops a new task into the `incoming` phase with an
	// auto-generated key. Status/type/NodeId are decided by the service (same path the
	// MCP upsert uses), so a kinded board gets a valid initial status, not a cold "Pending".
	public async Task<IActionResult> OnPostCreateAsync(string name, string? body, long priority, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		await _tasks.QuickAddAsync(ProjectKey, Board, name, body, priority, ct);

		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}
}
