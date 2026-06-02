using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Features;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Web.Mcp;

// MCP surface for the Tasks module: named board lifecycle + temporal node content.
// Boards are created explicitly (no auto-vivify). Node ops go through the generic
// temporal engine (optimistic concurrency by baseline, rename via prevKey,
// delta-since-cursor). Scopes: tasks:read / tasks:write. Feature: Tasks.
[McpServerToolType]
public static class TasksTools
{
	[McpServerTool(Name = "tasks.board_create", Title = "Create a task board")]
	[Description("Create a named task board in a project. `kind` sets the board role (free|spec|ideas|intake|work; default free) which drives the workflow — see tasks.workflow. `specBoard` (work boards only) names the spec board this board's tasks link into, so specRef targets are validated against it and the agent need not guess. Requires tasks:write.")]
	public static async Task<object> BoardCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, string? kind = null, string? description = null, string? specBoard = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		await ValidateSpecBoardAsync(boards, projectKey, kind ?? "free", specBoard, ct);
		var meta = await boards.CreateAsync(projectKey, board, description, kind ?? "free", specBoard, ct);
		return new { meta.ProjectKey, meta.Name, meta.Kind, meta.Description, meta.SpecBoard, meta.CreatedAt };
	}

	[McpServerTool(Name = "tasks.board_set_spec", Title = "Set a work board's spec board")]
	[Description("Set (or clear, when specBoard is omitted) the spec board a work board's tasks link into. The target must be a spec board. Makes the work->spec link explicit. Requires tasks:write.")]
	public static async Task<object> BoardSetSpecAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, string? specBoard = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		await EnsureBoard(boards, projectKey, board, ct);
		var meta = (await boards.FindAsync(projectKey, board, ct))!;
		await ValidateSpecBoardAsync(boards, projectKey, meta.Kind, specBoard, ct);
		var norm = string.IsNullOrWhiteSpace(specBoard) ? null : specBoard;
		return new { set = await boards.UpdateAsync(projectKey, board, m => m with { SpecBoard = norm }, ct), specBoard = norm };
	}

	// A specBoard link only makes sense on a work board and must point at an existing spec board.
	static async Task ValidateSpecBoardAsync(ITaskBoardStore boards, string projectKey, string kind, string? specBoard, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(specBoard)) return;
		if (WorkflowCatalog.ParseKind(kind) != BoardKind.Work)
			throw new ArgumentException($"specBoard applies only to a work board (this board's kind is '{kind}')");
		var target = await boards.FindAsync(projectKey, specBoard, ct)
			?? throw new ArgumentException($"spec board '{specBoard}' not found in project '{projectKey}'");
		if (WorkflowCatalog.ParseKind(target.Kind) != BoardKind.Spec)
			throw new ArgumentException($"'{specBoard}' is not a spec board (kind is '{target.Kind}')");
	}

	[McpServerTool(Name = "tasks.board_list", Title = "List task boards", ReadOnly = true)]
	[Description("List task boards in a project, each with its kind, specBoard (work->spec link, if set) and closed flag. Requires tasks:read.")]
	public static async Task<object> BoardListAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await boards.ListAsync(projectKey, ct);
		return new { boards = list.Select(b => new { b.Name, b.Kind, b.Description, b.SpecBoard, b.CreatedAt, closed = b.ClosedAt != null }).ToList() };
	}

	[McpServerTool(Name = "tasks.board_delete", Title = "Delete a task board", Destructive = true)]
	[Description("Delete a task board and its nodes. Requires tasks:write.")]
	public static async Task<object> BoardDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new { deleted = await boards.DeleteAsync(projectKey, board, ct) };
	}

	[McpServerTool(Name = "tasks.board_close", Title = "Close (archive) a task board")]
	[Description("Close a board: it rejects further writes (so agents stop writing to it by inertia) but stays readable; history is kept. Reopen with tasks.board_reopen. Requires tasks:write.")]
	public static async Task<object> BoardCloseAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new { closed = await boards.UpdateAsync(projectKey, board, m => m with { ClosedAt = DateTime.UtcNow }, ct) };
	}

	[McpServerTool(Name = "tasks.board_reopen", Title = "Reopen a closed task board")]
	[Description("Reopen a closed board so it accepts writes again. Requires tasks:write.")]
	public static async Task<object> BoardReopenAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new { reopened = await boards.UpdateAsync(projectKey, board, m => m with { ClosedAt = null }, ct) };
	}

	[McpServerTool(Name = "tasks.get", Title = "Get a board's nodes", ReadOnly = true)]
	[Description("""
		Return the active plan nodes of a board as a 1-to-3 level tree, ordered by priority
		then path. Top level carries `kind` (the board role) and `specBoard` (the spec board
		work tasks link into, if set). Each node carries key, nodeId, l1, l2, l3, depth,
		parentKey, status, type, title, body, priority, version, renamedFrom, plus its links:
		`spec` (spec nodes this task implements — task_spec), `blockedBy` (nodes blocking it),
		and on a spec board `linkedTasks` (tasks implementing it) plus the COMPUTED `delivery`
		roll-up (not_started|in_progress|done|done_with_defects). By default terminal/closed
		nodes (Done/Cancelled/…) are HIDDEN — pass includeClosed=true to include them; closed
		ancestors of a visible node are kept so the tree stays connected. `under` ("l1" or
		"l1/l2") restricts to that subtree. Requires tasks:read.
		""")]
	public static async Task<object> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards, IRelationStore relations,
		string projectKey, string board, bool includeClosed = false, string? under = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		await EnsureBoard(boards, projectKey, board, ct);

		var ctx = boards.GetContext(projectKey, board);
		var all = ctx.PlanNodes.ToList();
		var lineage = BuildLineage(all);
		var active = all.Where(n => n.ActiveTo == null).OrderBy(n => n.Priority).ThenBy(n => n.Key).ToList();
		var current = all.Count == 0 ? 0 : all.Max(n => n.Version);

		var meta = (await boards.FindAsync(projectKey, board, ct))!;
		var kind = WorkflowCatalog.ParseKind(meta.Kind);
		var delivery = kind == BoardKind.Spec ? await ComputeSpecDeliveryAsync(boards, relations, projectKey, active, ct) : null;

		var underKey = NormalizeUnder(under);
		var visible = FilterVisible(active, includeClosed, underKey);
		var index = await BuildNodeIndexAsync(boards, projectKey, ct);

		var nodes = new List<PlanNodeView>();
		foreach (var n in visible)
		{
			var id = TaskNodeId.TryParse(n.Key, out var pid) ? pid : null;
			var fromEdges = n.NodeId.Length > 0 ? await relations.ListAsync(projectKey, n.NodeId, "from", ct: ct) : [];
			var toEdges = n.NodeId.Length > 0 ? await relations.ListAsync(projectKey, n.NodeId, "to", ct: ct) : [];
			var spec = fromEdges.Where(e => e.Kind == "task_spec").Select(e => LinkRef(e.ToNodeId, index)).ToList();
			var blockedBy = toEdges.Where(e => e.Kind == "blocks").Select(e => LinkRef(e.FromNodeId, index)).ToList();
			var linkedTasks = kind == BoardKind.Spec ? toEdges.Where(e => e.Kind == "task_spec").Select(e => LinkRef(e.FromNodeId, index)).ToList() : null;
			nodes.Add(new PlanNodeView(
				Key: n.Key,
				NodeId: n.NodeId,
				L1: id?.PhaseKey ?? n.Key,
				L2: id?.WaveKey,
				L3: id?.TaskKey,
				Depth: id?.Depth ?? 1,
				ParentKey: id?.ParentKey,
				Status: n.Status,
				Type: n.Type,
				Title: n.Name,
				Body: n.Body,
				CommitRef: n.CommitRef,
				Priority: n.Priority,
				Version: n.Version,
				Delivery: delivery is not null && delivery.TryGetValue(n.Key, out var dv) ? dv : null,
				Spec: spec.Count > 0 ? spec : null,
				BlockedBy: blockedBy.Count > 0 ? blockedBy : null,
				LinkedTasks: linkedTasks is { Count: > 0 } ? linkedTasks : null,
				RenamedFrom: lineage.TryGetValue(n.Key, out var p) ? p : []));
		}
		return new PlanBoardView(current, kind.ToString().ToLowerInvariant(), meta.SpecBoard, nodes);
	}

	// Typed projections for tasks.get (camelCased by the MCP serializer). LinkDto resolves a
	// link target to its location; a `status` of "missing" means the target no longer exists.
	public sealed record LinkDto(string NodeId, string? Board, string? L1, string? L2, string? L3, string? Title, string Status);
	public sealed record PlanNodeView(
		string Key, string NodeId, string L1, string? L2, string? L3, int Depth, string? ParentKey,
		string Status, string Type, string Title, string Body, string? CommitRef, long Priority, long Version,
		string? Delivery, IReadOnlyList<LinkDto>? Spec, IReadOnlyList<LinkDto>? BlockedBy,
		IReadOnlyList<LinkDto>? LinkedTasks, IReadOnlyList<string> RenamedFrom);
	public sealed record PlanBoardView(long CurrentVersion, string Kind, string? SpecBoard, IReadOnlyList<PlanNodeView> Nodes);

	// Hide terminal (closed) nodes unless includeClosed; keep terminal ancestors of any
	// visible node so the tree stays connected. `underKey` (canonical) restricts to a subtree.
	static List<PlanNode> FilterVisible(List<PlanNode> active, bool includeClosed, string? underKey)
	{
		IEnumerable<PlanNode> scoped = active;
		if (underKey is not null)
			scoped = active.Where(n => n.Key == underKey || n.Key.StartsWith(underKey + "/", StringComparison.Ordinal));
		var pool = scoped.ToList();
		if (includeClosed) return pool;

		var keep = new HashSet<string>(StringComparer.Ordinal);
		foreach (var n in pool.Where(n => !WorkflowCatalog.IsTerminalSlug(n.Status)))
		{
			keep.Add(n.Key);
			// add ancestors (l1, l1/l2) so the path to a visible node survives
			var parts = n.Key.Split('/');
			for (var i = 1; i < parts.Length; i++)
				keep.Add(string.Join('/', parts.Take(i)));
		}
		return pool.Where(n => keep.Contains(n.Key)).ToList();
	}

	// Canonicalise an `under` path filter ("l1" or "l1/l2/l3") to a node key, or null.
	static string? NormalizeUnder(string? under) =>
		string.IsNullOrWhiteSpace(under) ? null : TaskNodeId.Parse(under).ToKey();

	// A resolvable reference to a node anywhere in the project (links cross boards).
	sealed record NodeRef(string Board, string BoardKind, string Key, string? L1, string? L2, string? L3, string Title, string Status, string Type);

	// nodeId -> NodeRef across every board in the project (links bind to nodeId, which is
	// globally unique, so a link target may live on another board).
	static async Task<Dictionary<string, NodeRef>> BuildNodeIndexAsync(ITaskBoardStore boards, string projectKey, CancellationToken ct)
	{
		var index = new Dictionary<string, NodeRef>(StringComparer.Ordinal);
		foreach (var b in await boards.ListAsync(projectKey, ct))
			foreach (var n in boards.GetContext(projectKey, b.Name).PlanNodes.Where(x => x.ActiveTo == null).ToList())
				if (n.NodeId.Length > 0)
				{
					var id = TaskNodeId.TryParse(n.Key, out var pid) ? pid : null;
					index[n.NodeId] = new NodeRef(b.Name, b.Kind, n.Key, id?.PhaseKey ?? n.Key, id?.WaveKey, id?.TaskKey, n.Name, n.Status, n.Type);
				}
		return index;
	}

	// Surface a link target: resolve the nodeId to its board/path/title, or mark it missing.
	static LinkDto LinkRef(string nodeId, Dictionary<string, NodeRef> index) =>
		index.TryGetValue(nodeId, out var r)
			? new LinkDto(nodeId, r.Board, r.L1, r.L2, r.L3, r.Title, r.Status)
			: new LinkDto(nodeId, null, null, null, null, null, "missing");

	// COMPUTED spec roll-up: a spec node's delivery status derives from the tasks linked
	// (task_spec) to it AND its descendants. not_started (no feature tasks) / in_progress
	// (some feature not Done) / done (all features Done, no open bug) / done_with_defects
	// (all features Done but an open bug remains). Type-aware: feature = build unit, bug =
	// defect. Subtree by key prefix, so parents aggregate children.
	static async Task<Dictionary<string, string>> ComputeSpecDeliveryAsync(
		ITaskBoardStore boards, IRelationStore relations, string projectKey, IReadOnlyList<PlanNode> specNodes, CancellationToken ct)
	{
		var byNodeId = new Dictionary<string, (string Type, string Status)>(StringComparer.Ordinal);
		foreach (var b in await boards.ListAsync(projectKey, ct))
			foreach (var n in boards.GetContext(projectKey, b.Name).PlanNodes.Where(x => x.ActiveTo == null).ToList())
				if (n.NodeId.Length > 0) byNodeId[n.NodeId] = (n.Type, n.Status);

		var tasksOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var s in specNodes)
			tasksOf[s.NodeId] = (await relations.ListAsync(projectKey, s.NodeId, "to", ct: ct))
				.Where(e => e.Kind == "task_spec").Select(e => e.FromNodeId).ToList();

		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var s in specNodes)
		{
			var taskIds = specNodes
				.Where(x => x.Key == s.Key || x.Key.StartsWith(s.Key + "/", StringComparison.Ordinal))
				.SelectMany(x => tasksOf.TryGetValue(x.NodeId, out var t) ? t : new List<string>())
				.Distinct();
			result[s.Key] = Delivery(taskIds.Where(byNodeId.ContainsKey).Select(id => byNodeId[id]).ToList());
		}
		return result;
	}

	static string Delivery(List<(string Type, string Status)> tasks)
	{
		var features = tasks.Where(t => t.Type == "feature").ToList();
		if (features.Count == 0) return "not_started";
		if (!features.All(f => WorkflowCatalog.KindOfSlug(f.Status) == StatusKind.TerminalOk)) return "in_progress";
		var openBug = tasks.Any(t => t.Type == "bug" && WorkflowCatalog.KindOfSlug(t.Status) == StatusKind.Open);
		return openBug ? "done_with_defects" : "done";
	}

	[McpServerTool(Name = "tasks.upsert", Title = "Upsert plan nodes")]
	[Description("""
		Declarative temporal upsert of plan nodes. Requires tasks:write.

		A plan is a 1-to-3 level tree. Address each node by path: l1 (required), optional l2,
		optional l3 (needs l2) — short anchor keys [a-z][a-z0-9_-]{0,99} — or an "l1/l2/l3"
		string in `key`. The path is the stable citation; create parents with/before children.
		Give each node a `title` (short heading) and `body` (markdown). Other fields: status
		(slug — see tasks.workflow for the board's kind), type (feature|bug on work boards),
		specRef (a spec NodeId — links a work task to the spec node it implements), blockedBy
		(a NodeId — marks this task Blocked by that one), commitRef?, priority? (sparse int,
		lower first), version (baseline you last saw; 0 = new). Rename via prevL1/prevL2/prevL3
		or prevKey. A cold call auto-creates the board.

		Returns { applied, currentVersion, inserted, closed, conflicts[], added[], updated[],
		removed[] }; added/updated carry the full node (key, nodeId, l1, l2, l3, depth,
		parentKey, status, type, title, body, commitRef, priority, version). The delta IS the
		fresh state since `sinceVersion` — advance your cursor and merge, no need to re-read.
		""")]
	public static async Task<object> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards, IRelationStore relations,
		string projectKey, string board,
		[Description("JSON array of node objects. A node may carry specRef (a spec NodeId) — on a work board this links the task to that spec node (task_spec edge).")] JsonElement nodes,
		long sinceVersion = 0, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		await boards.EnsureAsync(projectKey, board, ct); // auto-vivify on first write
		var meta = (await boards.FindAsync(projectKey, board, ct))!;
		if (meta.ClosedAt != null)
			throw new InvalidOperationException($"board '{board}' is closed — reopen it (tasks.board_reopen) before writing");

		var kind = WorkflowCatalog.ParseKind(meta.Kind);
		var ctx = boards.GetContext(projectKey, board);
		var prior = ctx.PlanNodes.Where(n => n.ActiveTo == null).ToList()
			.ToDictionary(n => n.Key, n => n, StringComparer.Ordinal);
		// Read-merge: fields omitted from the JSON inherit the prior active row, so a partial
		// update (e.g. only path + version + status) doesn't blank title/body/type/priority.
		var desired = ParseNodes(nodes, prior).Select(n => ApplyWorkflow(kind, n, prior)).ToArray();
		var specRefs = ParseNodeField(nodes, "specRef");
		var blockedBy = ParseNodeField(nodes, "blockedBy");
		RequireSpecLinks(kind, desired, prior, specRefs);
		await ValidateSpecRefsAsync(boards, projectKey, meta, specRefs, ct);
		await RequireBlockersAsync(relations, kind, projectKey, desired, blockedBy, ct);
		var r = await TemporalStore.UpsertAsync(ctx, desired, sinceVersion, ct: ct);
		if (r.Applied)
		{
			await boards.TouchAsync(projectKey, board, ct);
			await LinkRefsAsync(relations, projectKey, "task_spec", desired, specRefs, blockerIsFrom: false, ct);
			await LinkRefsAsync(relations, projectKey, "blocks", desired, blockedBy, blockerIsFrom: true, ct);
			await CloseBlocksOnLeaveAsync(relations, projectKey, desired, prior, ct);
			await RunDoneEffectsAsync(boards, relations, projectKey, kind, desired, ct);
		}
		return Serialize(r, kind);
	});

	// Validate each specRef target: it must resolve to an existing node on a spec board, and
	// (if this work board has a SpecBoard set) on that specific board — so a link can't point
	// at a non-spec node or the wrong spec board. Built from current state (the target already exists).
	static async Task ValidateSpecRefsAsync(ITaskBoardStore boards, string projectKey, TaskBoardMeta workBoard, Dictionary<string, string> specRefs, CancellationToken ct)
	{
		if (specRefs.Count == 0) return;
		var index = await BuildNodeIndexAsync(boards, projectKey, ct);
		foreach (var (key, refId) in specRefs)
		{
			if (!index.TryGetValue(refId, out var t))
				throw new ArgumentException($"specRef '{refId}' (node '{key}') does not resolve to any node");
			if (WorkflowCatalog.ParseKind(t.BoardKind) != BoardKind.Spec)
				throw new ArgumentException($"specRef '{refId}' (node '{key}') points to board '{t.Board}', which is not a spec board");
			if (workBoard.SpecBoard is { Length: > 0 } sb && t.Board != sb)
				throw new ArgumentException($"specRef '{refId}' (node '{key}') is on board '{t.Board}', but this work board links spec board '{sb}'");
		}
	}

	// FSM effect: when a work node reaches a TerminalOk status, auto-close any intake
	// issue that spawned it (reverse-traverse issue_task: the task is the `to` end).
	// Runs as a system action (no approve gate). Idempotent: already-closed issues skip.
	static async Task RunDoneEffectsAsync(ITaskBoardStore boards, IRelationStore relations, string projectKey, BoardKind kind, PlanNode[] desired, CancellationToken ct)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired.Where(n => WorkflowCatalog.KindOfSlug(n.Status) == StatusKind.TerminalOk))
		{
			// (a) close the intake issue(s) this task resolved (issue_task: task is the `to` end)
			foreach (var e in (await relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Where(e => e.Kind == "issue_task"))
				await SetActiveNodeStatusAsync(boards, projectKey, e.FromNodeId,
					(wf, node) => WorkflowCatalog.IsTerminalSlug(node.Status) ? null : wf?.Statuses.FirstOrDefault(s => s.Kind == StatusKind.TerminalOk)?.Slug, ct);

			// (b) unblock tasks this one was blocking (blocks: this is the blocker / `from` end).
			// Close the edge (history kept); if the blocked task has no blockers left, Blocked -> InProgress.
			foreach (var e in (await relations.ListAsync(projectKey, n.NodeId, "from", ct: ct)).Where(e => e.Kind == "blocks"))
			{
				await relations.CloseAsync(projectKey, "blocks", e.FromNodeId, e.ToNodeId, ct);
				var stillBlocked = (await relations.ListAsync(projectKey, e.ToNodeId, "to", ct: ct)).Any(x => x.Kind == "blocks");
				if (!stillBlocked)
					await SetActiveNodeStatusAsync(boards, projectKey, e.ToNodeId,
						(_, node) => string.Equals(node.Status, "Blocked", StringComparison.OrdinalIgnoreCase) ? "InProgress" : null, ct);
			}
		}
	}

	// Find the active node with this NodeId across the project's boards and move it to a
	// target status chosen by `pick` (null = leave as-is). System action (no approve gate).
	static async Task SetActiveNodeStatusAsync(ITaskBoardStore boards, string projectKey, string nodeId, Func<Workflow?, PlanNode, string?> pick, CancellationToken ct)
	{
		foreach (var b in await boards.ListAsync(projectKey, ct))
		{
			var ctx = boards.GetContext(projectKey, b.Name);
			var node = ctx.PlanNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault();
			if (node is null) continue;
			var wf = WorkflowCatalog.For(WorkflowCatalog.ParseKind(b.Kind), node.Type.Length == 0 ? null : node.Type);
			var target = pick(wf, node);
			if (target is null || string.Equals(target, node.Status, StringComparison.OrdinalIgnoreCase)) return;
			await TemporalStore.UpsertAsync(ctx, new[] { node with { Status = target } }, ct: ct);
			await boards.TouchAsync(projectKey, b.Name, ct);
			return;
		}
	}

	// Create relation edges from a per-node field after the upsert applies. task_spec
	// (specRef): task -> spec (blockerIsFrom=false). blocks (blockedBy): blocker -> task
	// (blockerIsFrom=true). Idempotent (RelationStore returns the active edge if present).
	static async Task LinkRefsAsync(IRelationStore relations, string projectKey, string kind, PlanNode[] desired, Dictionary<string, string> refs, bool blockerIsFrom, CancellationToken ct)
	{
		if (refs.Count == 0) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (key, other) in refs)
			if (byKey.TryGetValue(key, out var nid) && nid.Length > 0)
			{
				var (from, to) = blockerIsFrom ? (other, nid) : (nid, other);
				await relations.CreateAsync(projectKey, kind, from, to, ct);
			}
	}

	// Invariant: a work task in `Blocked` must name a blocker (blockedBy in this call, or
	// an already-active `blocks` edge into it). "Blocked requires a link."
	static async Task RequireBlockersAsync(IRelationStore relations, BoardKind kind, string projectKey, PlanNode[] desired, Dictionary<string, string> blockedBy, CancellationToken ct)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired)
		{
			if (!string.Equals(n.Status, "Blocked", StringComparison.OrdinalIgnoreCase)) continue;
			if (blockedBy.ContainsKey(n.Key)) continue; // a blocker is being linked now
			var hasActiveBlocker = (await relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Any(e => e.Kind == "blocks");
			if (!hasActiveBlocker)
				throw new ArgumentException($"a Blocked task must name a blocker — provide blockedBy (node '{n.Key}')");
		}
	}

	// Leaving Blocked manually closes the active `blocks` edges into the node (history kept).
	static async Task CloseBlocksOnLeaveAsync(IRelationStore relations, string projectKey, PlanNode[] desired, Dictionary<string, PlanNode> prior, CancellationToken ct)
	{
		foreach (var n in desired)
		{
			var wasBlocked = prior.TryGetValue(n.Key, out var cur) && string.Equals(cur.Status, "Blocked", StringComparison.OrdinalIgnoreCase);
			if (!wasBlocked || string.Equals(n.Status, "Blocked", StringComparison.OrdinalIgnoreCase)) continue;
			foreach (var e in (await relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Where(e => e.Kind == "blocks"))
				await relations.CloseAsync(projectKey, "blocks", e.FromNodeId, e.ToNodeId, ct);
		}
	}

	// Invariant: a NEW work feature/bug must link a spec node (specRef). Edits of an
	// existing node don't re-require it. Holds "no work task without a spec link".
	static void RequireSpecLinks(BoardKind kind, PlanNode[] desired, Dictionary<string, PlanNode> prior, Dictionary<string, string> specRefs)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired)
		{
			if (n.Type is not ("feature" or "bug")) continue;
			var isNew = !prior.ContainsKey(n.Key) && (n.PrevKey is null || !prior.ContainsKey(n.PrevKey));
			if (isNew && !specRefs.ContainsKey(n.Key))
				throw new ArgumentException($"a work {n.Type} must link a spec node — provide specRef (node '{n.Key}')");
		}
	}

	// Map node-key -> value of a per-node string field (specRef, blockedBy, ...), across
	// both the array and stringified-array param forms.
	static Dictionary<string, string> ParseNodeField(JsonElement nodes, string field)
	{
		using var doc = nodes.ValueKind == JsonValueKind.String
			? JsonDocument.Parse(nodes.GetString() ?? "")
			: (JsonDocument?)null;
		var arr = doc?.RootElement ?? nodes;
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		if (arr.ValueKind != JsonValueKind.Array) return map;
		foreach (var e in arr.EnumerateArray())
		{
			var v = ModuleMcp.OptStr(e, field);
			if (!string.IsNullOrWhiteSpace(v)) map[ResolveKey(e)] = v!;
		}
		return map;
	}

	// Default status, assign/carry the stable NodeId (new = fresh, edit = keep,
	// rename = inherit from source), and validate status/transition — the single
	// workflow validation point.
	static PlanNode ApplyWorkflow(BoardKind kind, PlanNode node, Dictionary<string, PlanNode> prior)
	{
		var type = node.Type.Length == 0 ? null : node.Type;
		var wf = WorkflowCatalog.For(kind, type);
		var n = node.Status.Length > 0 ? node : node with { Status = wf?.Initial ?? "Pending" };

		var current = prior.GetValueOrDefault(n.Key);
		var source = n.PrevKey is not null ? prior.GetValueOrDefault(n.PrevKey) : null;
		var nodeId = current?.NodeId is { Length: > 0 } cid ? cid
			: source?.NodeId is { Length: > 0 } sid ? sid
			: Guid.NewGuid().ToString("N");
		n = n with { NodeId = nodeId };

		var from = current?.Status ?? source?.Status;
		var res = WorkflowEngine.Validate(kind, type, from, n.Status, hasReason: !string.IsNullOrWhiteSpace(n.Body));
		if (!res.Ok) throw new ArgumentException(res.Error);
		return n;
	}

	[McpServerTool(Name = "tasks.delta", Title = "Plan delta since cursor", ReadOnly = true)]
	[Description("Return nodes added/updated/removed since `sinceVersion` (no writes). Requires tasks:read.")]
	public static async Task<object> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, long sinceVersion, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		await EnsureBoard(boards, projectKey, board, ct);

		var meta = (await boards.FindAsync(projectKey, board, ct))!;
		var ctx = boards.GetContext(projectKey, board);
		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<PlanNode>(), sinceVersion, ct: ct);
		return Serialize(r, WorkflowCatalog.ParseKind(meta.Kind));
	}

	[McpServerTool(Name = "tasks.workflow", Title = "Board workflow (kinds/statuses/transitions)", ReadOnly = true)]
	[Description("Return the workflow for a board: its kind and the task types it hosts, each with statuses (slug, name, kind=open|terminalok|terminalcancel), the initial status, and transitions (from, to, requiresApproval, requiresReason). Use this to learn the legal statuses before tasks.upsert. Requires tasks:read.")]
	public static async Task<object> WorkflowAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		await EnsureBoard(boards, projectKey, board, ct);

		var kind = WorkflowCatalog.ParseKind((await boards.FindAsync(projectKey, board, ct))!.Kind);
		return (object)new
		{
			kind = kind.ToString().ToLowerInvariant(),
			types = WorkflowCatalog.Types(kind).Select(w => new
			{
				type = w.Type,
				initial = w.Initial,
				statuses = w.Statuses.Select(s => new { slug = s.Slug, name = s.Name, kind = s.Kind.ToString().ToLowerInvariant() }).ToList(),
				transitions = w.Transitions.Select(t => new { from = t.From, to = t.To, requiresApproval = t.RequiresApproval, requiresReason = t.RequiresReason }).ToList(),
			}).ToList(),
		};
	});

	static async Task EnsureBoard(ITaskBoardStore boards, string projectKey, string board, CancellationToken ct)
	{
		if (!await boards.ExistsAsync(projectKey, board, ct))
			throw new InvalidOperationException($"task board '{board}' not found in project '{projectKey}'");
	}

	static object Serialize(TemporalUpsertResult<PlanNode> r, BoardKind kind) => new
	{
		applied = r.Applied,
		currentVersion = r.CurrentVersion,
		kind = kind.ToString().ToLowerInvariant(),
		inserted = r.Inserted,
		closed = r.Closed,
		conflicts = r.Conflicts.Select(c => new
		{
			key = c.Key,
			kind = c.Kind.ToString(),
			baselineVersion = c.BaselineVersion,
			activeVersion = c.ActiveVersion,
		}).ToList(),
		added = r.Added.Select(NodeDto).ToList(),
		updated = r.Updated.Select(NodeDto).ToList(),
		removed = r.Removed.ToList(),
	};

	// Delta projection of a node (no links/delivery — that's tasks.get). camelCased by the serializer.
	public sealed record PlanNodeDelta(
		string Key, string NodeId, string L1, string? L2, string? L3, int Depth, string? ParentKey,
		string Status, string Type, string Title, string Body, string? CommitRef, long Priority, long Version);

	static PlanNodeDelta NodeDto(PlanNode n)
	{
		var id = TaskNodeId.TryParse(n.Key, out var p) ? p : null;
		return new PlanNodeDelta(
			Key: n.Key,
			NodeId: n.NodeId,
			L1: id?.PhaseKey ?? n.Key,
			L2: id?.WaveKey,
			L3: id?.TaskKey,
			Depth: id?.Depth ?? 1,
			ParentKey: id?.ParentKey,
			Status: n.Status,
			Type: n.Type,
			Title: n.Name,
			Body: n.Body,
			CommitRef: n.CommitRef,
			Priority: n.Priority,
			Version: n.Version);
	}

	// Parse the node array, merging omitted fields from the prior active row (read-merge):
	// only the path (l1/.. or key) is ever required; any field absent from the JSON keeps
	// its prior value, so a status-only update needs just path + version + status. A field
	// present-but-empty ("title":"") is an explicit clear. New nodes inherit nothing.
	static PlanNode[] ParseNodes(JsonElement nodes, IReadOnlyDictionary<string, PlanNode> prior)
	{
		// MCP clients sometimes pass the array as a JSON *string* (the param is an
		// untyped JsonElement, so the client may stringify it); accept both forms.
		using var doc = nodes.ValueKind == JsonValueKind.String
			? JsonDocument.Parse(nodes.GetString() ?? "")
			: (JsonDocument?)null;
		var arr = doc?.RootElement ?? nodes;
		if (arr.ValueKind != JsonValueKind.Array)
			throw new ArgumentException($"nodes must be a JSON array (got {arr.ValueKind})");
		var list = new List<PlanNode>();
		foreach (var e in arr.EnumerateArray())
		{
			var key = ResolveKey(e);
			var prevKey = ResolvePrevKey(e);
			var cur = prior.GetValueOrDefault(key) ?? (prevKey is not null ? prior.GetValueOrDefault(prevKey) : null);
			list.Add(new PlanNode
			{
				Key = key,
				Version = ModuleMcp.OptLong(e, "version", 0),
				Status = Has(e, "status") ? ModuleMcp.OptStr(e, "status") ?? string.Empty : cur?.Status ?? string.Empty,
				Type = Has(e, "type") ? (ModuleMcp.OptStr(e, "type") ?? string.Empty).ToLowerInvariant() : cur?.Type ?? string.Empty,
				Name = Has(e, "title") ? ModuleMcp.OptStr(e, "title") ?? string.Empty : cur?.Name ?? string.Empty,
				Body = Has(e, "body") ? ModuleMcp.OptStr(e, "body") ?? string.Empty : cur?.Body ?? string.Empty,
				CommitRef = Has(e, "commitRef") ? ModuleMcp.OptStr(e, "commitRef") : cur?.CommitRef,
				Priority = Has(e, "priority") ? ModuleMcp.OptLong(e, "priority", 0) : cur?.Priority ?? 0,
				PrevKey = prevKey,
			});
		}
		return list.ToArray();

		static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);
	}

	// A node's identity is its 1-to-3 level path of anchor keys. Preferred input is the
	// structured form (l1 + optional l2 + optional l3); a canonical "l1/l2/l3" string in
	// `key` is accepted as an alternative. Both are validated/canonicalised via TaskNodeId.
	static string ResolveKey(JsonElement e)
	{
		var l1 = ModuleMcp.OptStr(e, "l1");
		if (l1 is not null)
			return new TaskNodeId(l1, ModuleMcp.OptStr(e, "l2"), ModuleMcp.OptStr(e, "l3")).ToKey();

		var key = ModuleMcp.OptStr(e, "key");
		if (key is not null)
			return TaskNodeId.Parse(key).ToKey();

		throw new ArgumentException("each node needs 'l1' (+optional l2/l3) or a 'key' path");
	}

	static string? ResolvePrevKey(JsonElement e)
	{
		var prevL1 = ModuleMcp.OptStr(e, "prevL1");
		if (prevL1 is not null)
			return new TaskNodeId(prevL1, ModuleMcp.OptStr(e, "prevL2"), ModuleMcp.OptStr(e, "prevL3")).ToKey();

		var prevKey = ModuleMcp.OptStr(e, "prevKey");
		return prevKey is not null ? TaskNodeId.Parse(prevKey).ToKey() : null;
	}

	// Active node key -> chain of prior keys it was renamed from (walk PrevKey edges
	// across the full revision history).
	static Dictionary<string, List<string>> BuildLineage(List<PlanNode> all)
	{
		var edge = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var g in all.GroupBy(n => n.Key, StringComparer.Ordinal))
		{
			var birth = g.OrderBy(n => n.Version).First();
			if (!string.IsNullOrEmpty(birth.PrevKey))
				edge[g.Key] = birth.PrevKey!;
		}

		var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var key in edge.Keys)
		{
			var chain = new List<string>();
			var cur = key;
			var guard = 0;
			while (edge.TryGetValue(cur, out var prev) && guard++ < 1000)
			{
				chain.Add(prev);
				cur = prev;
			}
			result[key] = chain;
		}
		return result;
	}
}
