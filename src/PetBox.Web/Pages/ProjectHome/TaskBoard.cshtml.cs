using System.Text.RegularExpressions;
using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one task board (/ui/{ws}/{project}/tasks/{board}). Shows
// the currently-active plan nodes (ActiveTo == null) ordered by sparse Priority
// then Key. Existence is checked against metadata first so we don't auto-vivify
// a phantom file for an unknown board name.
[Authorize]
public sealed class TaskBoardModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ITaskBoardStore _store;

	public TaskBoardModel(FeatureFlags features, ITaskBoardStore store)
	{
		_features = features;
		_store = store;
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
		if (!await _store.ExistsAsync(ProjectKey, Board, ct)) return NotFound();

		var ctx = _store.GetContext(ProjectKey, Board);
		var flat = await ctx.PlanNodes
			.Where(n => n.ActiveTo == null)
			.ToListAsync(ct);

		Nodes = OrderHierarchically(flat, out var keepVisible);
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
	// auto-generated key (slug of the name + short unique suffix).
	public async Task<IActionResult> OnPostCreateAsync(string name, string? body, long priority, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _store.ExistsAsync(ProjectKey, Board, ct)) return NotFound();

		if (!string.IsNullOrWhiteSpace(name))
		{
			var key = new TaskNodeId("incoming", GenKey(name), null).ToKey();
			var ctx = _store.GetContext(ProjectKey, Board);
			await TemporalStore.UpsertAsync(ctx, new[]
			{
				new PlanNode { Key = key, Version = 0, Status = "Pending", Name = name.Trim(), Body = body?.Trim() ?? string.Empty, Priority = priority },
			}, ct: ct);
		}

		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}

	static string GenKey(string name)
	{
		var ascii = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
		if (ascii.Length == 0 || !char.IsLetter(ascii[0])) ascii = "task-" + ascii;
		ascii = ascii.Trim('-');
		if (ascii.Length > 32) ascii = ascii[..32].Trim('-'); // keep keys short/readable
		return $"{ascii}-{Guid.NewGuid():N}"[..(Math.Min(ascii.Length, 32) + 7)];
	}
}
