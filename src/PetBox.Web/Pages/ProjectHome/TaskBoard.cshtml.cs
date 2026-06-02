using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
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
	// keys of Done/Cancelled nodes that must stay visible under "active only"
	// because a descendant is still open (else the children would orphan).
	public IReadOnlyList<PlanNode> Nodes { get; private set; } = [];
	public IReadOnlySet<string> ClosedWithActiveDescendant { get; private set; }
		= new HashSet<string>(StringComparer.Ordinal);

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		var flat = await _tasks.ListActiveNodesAsync(ProjectKey, Board, ct);

		Nodes = OrderHierarchically([.. flat], out var keepVisible);
		ClosedWithActiveDescendant = keepVisible;
		return Page();
	}

	// Render order is the plan tree itself — DFS by parentKey, siblings ordered by
	// Priority then Key. A flat priority sort (the previous behaviour) let a
	// low-priority child of an early phase visually drift past a later phase, so it
	// looked like it belonged to that phase (finding D11). DFS keeps every node
	// under its parent regardless of how its priority compares across the board.
	static List<PlanNode> OrderHierarchically(
		List<PlanNode> nodes, out IReadOnlySet<string> closedWithActiveDescendant)
	{
		var byKey = new Dictionary<string, PlanNode>(StringComparer.Ordinal);
		foreach (var n in nodes) byKey[n.Key] = n;

		static string? ParentOf(PlanNode n) => TaskNodeId.TryParse(n.Key, out var id) ? id!.ParentKey : null;

		// Children grouped by parent key, each sibling list ordered by priority then key.
		var childMap = nodes
			.GroupBy(ParentOf)
			.ToDictionary(
				g => g.Key ?? string.Empty,
				g => (IReadOnlyList<PlanNode>)g
					.OrderBy(n => n.Priority)
					.ThenBy(n => n.Key, StringComparer.Ordinal)
					.ToList(),
				StringComparer.Ordinal);

		// Roots: phase-level nodes plus any orphan whose parent isn't on the board.
		var roots = nodes
			.Where(n => { var pk = ParentOf(n); return pk is null || !byKey.ContainsKey(pk); })
			.OrderBy(n => n.Priority)
			.ThenBy(n => n.Key, StringComparer.Ordinal)
			.ToList();

		var ordered = new List<PlanNode>(nodes.Count);
		var closedKeep = new HashSet<string>(StringComparer.Ordinal);

		// Keys are hierarchical (a parent key is a strict prefix), so the tree is
		// acyclic and recursion is bounded at depth 3. Returns whether the subtree
		// holds a non-closed node, so a closed parent of open work stays visible.
		bool Emit(PlanNode node)
		{
			ordered.Add(node);
			var closed = WorkflowCatalog.IsTerminalSlug(node.Status);
			var hasActiveDescendant = false;
			if (childMap.TryGetValue(node.Key, out var kids))
			{
				foreach (var kid in kids)
					hasActiveDescendant |= Emit(kid);
			}
			if (closed && hasActiveDescendant) closedKeep.Add(node.Key);
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
