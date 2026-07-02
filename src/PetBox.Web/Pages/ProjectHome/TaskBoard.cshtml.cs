using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.Shared;

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
	readonly ICommentService _comments;

	public TaskBoardModel(FeatureFlags features, ITasksService tasks, ICommentService comments)
	{
		_features = features;
		_tasks = tasks;
		_comments = comments;
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

	// Per-node discussion thread, DFS-flattened to (comment, depth) so the view renders it
	// flat with an indent — the same shape as the plan-node list. Empty for nodes with no
	// comments. Read-only in v1 (writes go through the comments.* MCP tools). Rendered via
	// the shared _CommentThread partial (same flattener as the node detail page).
	public IReadOnlyDictionary<string, IReadOnlyList<CommentLine>> CommentThreads { get; private set; }
		= new Dictionary<string, IReadOnlyList<CommentLine>>(StringComparer.Ordinal);

	// View mode: the default part_of TREE, or the tag-groups PROJECTION (board-tag-grouping).
	// `?view=tags&by=area,concern` selects an ordered list of tag namespaces; the projection
	// is a pure view over the same nodes and never touches part_of (tag-grouping-is-projection).
	[BindProperty(SupportsGet = true, Name = "view")]
	public string ViewMode { get; set; } = "tree";

	[BindProperty(SupportsGet = true, Name = "by")]
	public string? By { get; set; }

	public bool IsTagView { get; private set; }
	public IReadOnlyList<string> GroupDims { get; private set; } = []; // ordered namespaces actually applied
	public IReadOnlyList<GroupRow> GroupRows { get; private set; } = []; // flattened tag-groups pane

	public bool ShowQuickAdd { get; private set; }

	// The board's kind (simple|spec|ideas|intake|work), surfaced so the UI can show it
	// explicitly — a simple board's lightweight statuses otherwise look like "broken
	// statuses" rather than a deliberate board kind.
	public BoardKind Kind { get; private set; }

	// One flattened row of the tag-groups pane: a group HEADER (Node null) at nesting `Depth`,
	// or a node CARD (Node set) sitting just under its deepest group. Flattening keeps the
	// Razor a single loop — the same shape the part_of pane already renders.
	public sealed record GroupRow(int Depth, string? GroupKey, string? Delivery, PlanNodeView? Node);

	// Everything the shared _PlanNodeCard partial needs to render one node card in either
	// pane. `Depth` drives the indent (part_of depth in the tree pane, 0 in the tag-groups
	// pane — grouping is the structure there). `HasChildren` shows the collapse caret (tree
	// only). The tree-interactivity data-* (parent/closed/keep-visible) are inert in the tag
	// pane because ts/board.ts binds only to the tree's board-nodes list.
	public sealed record PlanNodeCard(
		string WorkspaceKey, string ProjectKey, string Board, PlanNodeView Node,
		int Depth, bool Closed, bool KeepVisible, bool HasChildren,
		IReadOnlyList<CommentLine>? Thread);

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		var kind = await _tasks.ResolveKindAsync(ProjectKey, Board, ct);
		Kind = kind;
		ShowQuickAdd = MethodologyPresets.QuickAddAllowed(kind);

		// includeClosed: we render closed nodes too (the "active only" toggle hides them
		// client-side); GetAsync supplies each node's part_of parent + depth.
		var view = await _tasks.GetAsync(ProjectKey, Board, includeClosed: true, ct: ct);
		Nodes = OrderHierarchically([.. view.Nodes], out var keepVisible);
		ClosedWithActiveDescendant = keepVisible;

		// One query for every comment on the board, grouped by owning node; DFS-flatten each
		// node's thread by parentId so the view just iterates (no per-node N+1).
		var byNode = await _comments.ListForBoardAsync(ProjectKey, Board, ct);
		CommentThreads = byNode
			.ToDictionary(g => g.Key, g => CommentThread.Flatten(g), StringComparer.Ordinal);

		// Tag-groups projection: only when explicitly requested with a valid dimension list.
		// Bad/empty `by` silently falls back to the tree (the service would reject it) — the
		// view stays explicit, no implicit redirects.
		var dims = (By ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (string.Equals(ViewMode, "tags", StringComparison.OrdinalIgnoreCase) && dims.Length > 0)
		{
			try
			{
				var grouped = await _tasks.GetGroupedAsync(ProjectKey, Board, dims, ct);
				var byKey = Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
				GroupDims = grouped.GroupBy;
				GroupRows = FlattenGroups(grouped.Groups, 0, byKey);
				IsTagView = true;
			}
			catch (ArgumentException) { /* invalid namespace → stay on the tree view */ }
		}
		return Page();
	}

	// Depth-first flatten of the nested tag groups into header/card rows. A leaf group emits a
	// header then a card row per node (looked up by key); an inner group emits a header then
	// recurses its sub-groups one level deeper.
	static List<GroupRow> FlattenGroups(IReadOnlyList<TagGroup> groups, int depth, IReadOnlyDictionary<string, PlanNodeView> byKey)
	{
		var rows = new List<GroupRow>();
		foreach (var g in groups)
		{
			rows.Add(new GroupRow(depth, g.Key, g.Delivery, null));
			if (g.SubGroups.Count > 0)
				rows.AddRange(FlattenGroups(g.SubGroups, depth + 1, byKey));
			else
				foreach (var k in g.NodeKeys)
					if (byKey.TryGetValue(k, out var node))
						rows.Add(new GroupRow(depth + 1, null, null, node));
		}
		return rows;
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
			var closed = MethodologyPresets.IsTerminalSlug(node.Status);
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

		var kind = await _tasks.ResolveKindAsync(ProjectKey, Board, ct);
		if (!MethodologyPresets.QuickAddAllowed(kind)) return BadRequest();

		await _tasks.QuickAddAsync(ProjectKey, Board, name, body, priority, ct);

		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}
}
