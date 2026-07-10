using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one task board (/ui/{ws}/{project}/tasks/{board}). Shows
// the currently-active plan nodes (ActiveTo == null) in plan-tree order. Reads and
// the quick-add write both go through ITasksService — the page never opens the DB
// context itself, so quick-add gets the same NodeId/status handling the MCP path does.
[Authorize(Policy = "WorkspaceMember")]
public sealed class TaskBoardModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;
	readonly ICommentService _comments;
	readonly ISettingsResolver _settings;

	public TaskBoardModel(FeatureFlags features, ITasksService tasks, ICommentService comments, ISettingsResolver settings)
	{
		_features = features;
		_tasks = tasks;
		_comments = comments;
		_settings = settings;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
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
	// comments. Read-only in v1 (writes go through the comments_* MCP tools). Rendered via
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

	// Set when a comment mutation (add/reply/edit/delete) was rejected — a guard violation or
	// an optimistic-concurrency conflict re-renders the board with the message inline rather
	// than silently dropping it (mirrors TaskBoardNodeModel.Error / edit-respects-guards).
	public string? Error { get; private set; }

	// The project's commit-view URL template (RepoSettings, Scope.Project). When set, the commit-ref
	// chip on each card links to it and commit hashes in node/comment bodies autolink. Empty = off.
	public string? CommitUrlTemplate { get; private set; }

	// Resolved `[[slug]]` mentions across all card bodies + comment bodies (node-ref-autolink),
	// keyed by the mentioned slug. Threaded into each card so the renderer links resolvable
	// mentions. Empty when nothing resolved.
	public IReadOnlyDictionary<string, NodeRefTarget> NodeRefs { get; private set; }
		= new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);

	// The board's EFFECTIVE process, resolved through MethodologyRuntime — the same seam
	// the MCP tools / TasksService use (project definition first, preset fallback), so a
	// definition-declared custom kind renders its own statuses/terminality instead of
	// falling back to the Simple preset. KindSlug is the stored board kind (the runtime
	// lookup key); KindName is what the badge shows (a defined kind verbatim, else the
	// parsed preset name — `free`/unknown read as `simple`, exactly as before).
	public MethodologyRuntime Runtime { get; private set; } = MethodologyRuntime.PresetsOnly;
	public string? KindSlug { get; private set; }
	public string KindName { get; private set; } = string.Empty;

	// closed-board-disabled-display: null = open. Mirrors the list/sidebar closed badge
	// (TaskBoardMeta.ClosedAt) onto this content page — the write path already rejects
	// (TasksService.UpsertAsync) a closed board, so this drives the badge + hides quick-add.
	public DateTime? ClosedAt { get; private set; }

	// The board's workflow surface (per-type FSM blocks) + its JSON island for the "View
	// workflow" modal. WorkflowBlocks drives the header triggers (one per block); WorkflowJson
	// is the payload ts/workflow-viz.ts renders. Resolved through MethodologyRuntime, so
	// user-defined methodologies visualize out of the box.
	public IReadOnlyList<WorkflowBlock> WorkflowBlocks { get; private set; } = [];
	public string? WorkflowJson { get; private set; }

	// One flattened row of the tag-groups pane: a group HEADER (Node null) at nesting `Depth`,
	// or a node CARD (Node set) sitting just under its deepest group. Flattening keeps the
	// Razor a single loop — the same shape the part_of pane already renders.
	public sealed record GroupRow(int Depth, string? GroupKey, string? Delivery, PlanNodeView? Node);

	// Everything the shared _PlanNodeCard partial needs to render one node card in either
	// pane. `Runtime` + `KindSlug` let the card classify statuses per the board's EFFECTIVE
	// kind (definition first, preset fallback). `Depth` drives the indent (part_of depth in
	// the tree pane, 0 in the tag-groups pane — grouping is the structure there).
	// `HasChildren` shows the collapse caret (tree only). The tree-interactivity data-*
	// (parent/closed/keep-visible) are inert in the tag pane because ts/board.ts binds only
	// to the tree's board-nodes list.
	public sealed record PlanNodeCard(
		string WorkspaceKey, string ProjectKey, string Board, MethodologyRuntime Runtime,
		string? KindSlug, PlanNodeView Node,
		int Depth, bool Closed, bool KeepVisible, bool HasChildren,
		IReadOnlyList<CommentLine>? Thread, string? CommitUrlTemplate = null,
		IReadOnlyDictionary<string, NodeRefTarget>? NodeRefs = null);

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();
		return await LoadAsync(ct);
	}

	// comments-ui-edit: add a comment (or, when parentId is set, a reply) under `nodeId` — a
	// hidden form field, since this page renders MANY node cards (unlike the node detail page,
	// which resolves its one node from the bound route). Goes through ICommentService.AddAsync,
	// the low-ceremony UI door (the comments_upsert MCP verb shares the same guards).
	public async Task<IActionResult> OnPostCommentAddAsync(string nodeId, string? parentId, string body, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		try
		{
			var author = User.Identity?.Name ?? "system";
			var result = await _comments.AddAsync(ProjectKey, Board, nodeId, parentId, author, body, tags: null, ct);
			if (!result.Applied)
				return await LoadAsync(ct, "Could not add the comment — refresh and try again.");
		}
		catch (ArgumentException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}

	// comments-ui-edit: edit a comment's body in place. `version` is the watermark baseline the
	// form rendered with; a stale one (Applied:false) is surfaced as Error, not clobbered.
	public async Task<IActionResult> OnPostCommentEditAsync(string id, string body, long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		try
		{
			var result = await _comments.EditAsync(ProjectKey, Board, id, body, tags: null, version, ct);
			if (!result.Applied)
				return await LoadAsync(ct, "This comment changed since the page was opened — refresh and redo your edit.");
		}
		catch (ArgumentException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}

	// comments-ui-edit: soft-delete a comment. Rejected (InvalidOperationException) while it
	// still has active replies — surfaced inline instead of a raw 500.
	public async Task<IActionResult> OnPostCommentDeleteAsync(string id, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		try
		{
			await _comments.DeleteAsync(ProjectKey, Board, id, ct);
		}
		catch (InvalidOperationException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}

	// Shared load for the GET and the comment-mutation error re-render: everything OnGetAsync
	// used to do inline, now reusable so a rejected comment mutation can re-render the SAME
	// board with Error set, instead of a bare redirect that drops the message.
	async Task<IActionResult> LoadAsync(CancellationToken ct, string? error = null)
	{
		Error = error;
		(Runtime, KindSlug, ClosedAt) = await ResolveProcessAsync(ct);
		KindName = Runtime.KindName(KindSlug);
		// closed-board-disabled-display: a closed board never shows quick-add, regardless of
		// what the kind would otherwise allow — mirrors the server-side reject in UpsertAsync.
		ShowQuickAdd = Runtime.QuickAddAllowed(KindSlug) && ClosedAt is null;

		// Project-scoped commit-view template (cascades to workspace/system); empty when unset.
		CommitUrlTemplate = (await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey, ct)).CommitUrlTemplate;

		// The board's FSM surface, embedded for the "View workflow" modal (a few KB — no extra endpoint).
		var workflow = await _tasks.GetBoardWorkflowAsync(ProjectKey, Board, ct);
		WorkflowBlocks = workflow.Workflows;
		WorkflowJson = WorkflowGraphJson.Serialize(workflow);

		// includeClosed: we render closed nodes too (the "active only" toggle hides them
		// client-side); GetAsync supplies each node's part_of parent + depth.
		var view = await _tasks.GetAsync(ProjectKey, Board, includeClosed: true, ct: ct);
		Nodes = OrderHierarchically([.. view.Nodes], Runtime, KindSlug, out var keepVisible);
		ClosedWithActiveDescendant = keepVisible;

		// One query for every comment on the board, grouped by owning node; DFS-flatten each
		// node's thread by parentId so the view just iterates (no per-node N+1).
		var byNode = await _comments.ListForBoardAsync(ProjectKey, Board, ct);
		CommentThreads = byNode
			.ToDictionary(g => g.Key, g => CommentThread.Flatten(g), StringComparer.Ordinal);

		// Resolve `[[slug]]` mentions across every card body + every comment body in ONE batch
		// (node-ref-autolink), so each card's renderer can link resolvable mentions.
		var bodies = Nodes.Select(n => (string?)n.Body)
			.Concat(byNode.SelectMany(g => g).Select(c => (string?)c.Body));
		NodeRefs = await NodeRefMap.BuildAsync(_tasks, WorkspaceKey, ProjectKey, bodies, ct);

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

	// The board's effective process context: board-scoped MethodologyRuntime (instance
	// rules when membership is set) plus this board's stored kind slug. ListBoardsAsync
	// supplies the raw slug; this page must not open the store directly.
	async Task<(MethodologyRuntime Runtime, string? KindSlug, DateTime? ClosedAt)> ResolveProcessAsync(CancellationToken ct)
	{
		var meta = (await _tasks.ListBoardsAsync(ProjectKey, ct))
			.FirstOrDefault(b => string.Equals(b.Name, Board, StringComparison.Ordinal));
		var runtime = meta is null
			? await _tasks.GetRuntimeAsync(ProjectKey, ct)
			: await _tasks.GetRuntimeForBoardAsync(ProjectKey, Board, ct);
		return (runtime, meta?.Kind, meta?.ClosedAt);
	}

	// Render order is the plan tree itself — DFS by part_of parent, siblings ordered by
	// Priority then Key. A flat priority sort let a low-priority child of an early branch
	// visually drift past a later one (finding D11). DFS keeps every node under its parent.
	static List<PlanNodeView> OrderHierarchically(
		List<PlanNodeView> nodes, MethodologyRuntime runtime, string? kindSlug,
		out IReadOnlySet<string> closedWithActiveDescendant)
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
			var closed = runtime.IsTerminalStatus(kindSlug, node.Status);
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

		var (runtime, kindSlug, closedAt) = await ResolveProcessAsync(ct);
		if (!runtime.QuickAddAllowed(kindSlug) || closedAt is not null) return BadRequest();

		await _tasks.QuickAddAsync(ProjectKey, Board, name, body, priority, ct);

		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}
}
