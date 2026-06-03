using System.Text.RegularExpressions;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Validation;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services;

// The one implementation of ITasksService: the single door to the task store. All the
// domain logic that used to live in the MCP tool layer (spec validation, workflow
// application, blocker/spec-link invariants, delivery roll-up, FSM effects) lives here,
// so the MCP tools, Razor pages and REST share exactly one code path into the data.
public sealed partial class TasksService : ITasksService
{
	readonly ITaskBoardStore _boards;
	readonly IRelationStore _relations;
	readonly ITagStore _tags;

	// Dependency-free declarative invariants (immutable NodeId/type). Static — no state.
	static readonly PlanNodeChangeValidator ChangeValidator = new();

	public TasksService(ITaskBoardStore boards, IRelationStore relations, ITagStore tags)
	{
		_boards = boards;
		_relations = relations;
		_tags = tags;
	}

	// ---- board lifecycle ----

	public async Task<TaskBoardMeta> CreateBoardAsync(string projectKey, string board, string? kind, string? description, string? specBoard, CancellationToken ct = default)
	{
		await ValidateSpecBoardAsync(projectKey, kind ?? "free", specBoard, ct);
		return await _boards.CreateAsync(projectKey, board, description, kind ?? "free", specBoard, ct);
	}

	public async Task<(bool Set, string? SpecBoard)> SetSpecBoardAsync(string projectKey, string board, string? specBoard, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		await ValidateSpecBoardAsync(projectKey, meta.Kind, specBoard, ct);
		var norm = string.IsNullOrWhiteSpace(specBoard) ? null : specBoard;
		var set = await _boards.UpdateAsync(projectKey, board, m => m with { SpecBoard = norm }, ct);
		return (set, norm);
	}

	public Task<IReadOnlyList<TaskBoardMeta>> ListBoardsAsync(string projectKey, CancellationToken ct = default) =>
		_boards.ListAsync(projectKey, ct);

	public Task<bool> DeleteBoardAsync(string projectKey, string board, CancellationToken ct = default) =>
		_boards.DeleteAsync(projectKey, board, ct);

	public Task<bool> SetClosedAsync(string projectKey, string board, bool closed, CancellationToken ct = default) =>
		_boards.UpdateAsync(projectKey, board, m => m with { ClosedAt = closed ? DateTime.UtcNow : null }, ct);

	public Task<bool> BoardExistsAsync(string projectKey, string board, CancellationToken ct = default) =>
		_boards.ExistsAsync(projectKey, board, ct);

	public async Task<BoardKind> ResolveKindAsync(string projectKey, string board, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		return WorkflowCatalog.ParseKind((await _boards.FindAsync(projectKey, board, ct))!.Kind);
	}

	// A specBoard link only makes sense on a work board and must point at an existing spec board.
	async Task ValidateSpecBoardAsync(string projectKey, string kind, string? specBoard, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(specBoard)) return;
		if (WorkflowCatalog.ParseKind(kind) != BoardKind.Work)
			throw new ArgumentException($"specBoard applies only to a work board (this board's kind is '{kind}')");
		var target = await _boards.FindAsync(projectKey, specBoard, ct)
			?? throw new ArgumentException($"spec board '{specBoard}' not found in project '{projectKey}'");
		if (WorkflowCatalog.ParseKind(target.Kind) != BoardKind.Spec)
			throw new ArgumentException($"'{specBoard}' is not a spec board (kind is '{target.Kind}')");
	}

	async Task EnsureBoard(string projectKey, string board, CancellationToken ct)
	{
		if (!await _boards.ExistsAsync(projectKey, board, ct))
			throw new InvalidOperationException($"task board '{board}' not found in project '{projectKey}'");
	}

	// ---- read: tree view ----

	public async Task<PlanBoardView> GetAsync(string projectKey, string board, bool includeClosed = false, string? under = null, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);

		var ctx = _boards.GetContext(projectKey);
		var all = ctx.PlanNodes.Where(n => n.Board == board).ToList();
		var lineage = BuildLineage(all);
		var active = all.Where(n => n.ActiveTo == null).OrderBy(n => n.Priority).ThenBy(n => n.Key).ToList();
		var current = all.Count == 0 ? 0 : all.Max(n => n.Version);

		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		var kind = WorkflowCatalog.ParseKind(meta.Kind);
		var delivery = kind == BoardKind.Spec ? await ComputeSpecDeliveryAsync(projectKey, active, ct) : null;

		var underKey = NormalizeUnder(under);
		var visible = FilterVisible(active, includeClosed, underKey);
		var index = await BuildNodeIndexAsync(projectKey, ct);
		var tagsByNode = await _tags.BoardTagsAsync(projectKey, board, ct);

		var nodes = new List<PlanNodeView>();
		foreach (var n in visible)
		{
			var id = TaskNodeId.TryParse(n.Key, out var pid) ? pid : null;
			var fromEdges = n.NodeId.Length > 0 ? await _relations.ListAsync(projectKey, n.NodeId, "from", ct: ct) : [];
			var toEdges = n.NodeId.Length > 0 ? await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct) : [];
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
				RenamedFrom: lineage.TryGetValue(n.Key, out var p) ? p : [],
				Tags: tagsByNode[n.NodeId].OrderBy(t => t, StringComparer.Ordinal).ToList()));
		}
		return new PlanBoardView(current, kind.ToString().ToLowerInvariant(), meta.SpecBoard, nodes);
	}

	public Task<IReadOnlyList<PlanNode>> ListActiveNodesAsync(string projectKey, string board, CancellationToken ct = default)
	{
		var ctx = _boards.GetContext(projectKey);
		IReadOnlyList<PlanNode> active = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList();
		return Task.FromResult(active);
	}

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
			var parts = n.Key.Split('/');
			for (var i = 1; i < parts.Length; i++)
				keep.Add(string.Join('/', parts.Take(i)));
		}
		return pool.Where(n => keep.Contains(n.Key)).ToList();
	}

	static string? NormalizeUnder(string? under) =>
		string.IsNullOrWhiteSpace(under) ? null : TaskNodeId.Parse(under).ToKey();

	// A resolvable reference to a node anywhere in the project (links cross boards).
	sealed record NodeRef(string Board, string BoardKind, string Key, string? L1, string? L2, string? L3, string Title, string Status, string Type);

	// nodeId -> NodeRef across every board in the project (links bind to nodeId, which is
	// globally unique, so a link target may live on another board).
	async Task<Dictionary<string, NodeRef>> BuildNodeIndexAsync(string projectKey, CancellationToken ct)
	{
		var index = new Dictionary<string, NodeRef>(StringComparer.Ordinal);
		var ctx = _boards.GetContext(projectKey);
		foreach (var b in await _boards.ListAsync(projectKey, ct))
			foreach (var n in ctx.PlanNodes.Where(x => x.Board == b.Name && x.ActiveTo == null).ToList())
				if (n.NodeId.Length > 0)
				{
					var id = TaskNodeId.TryParse(n.Key, out var pid) ? pid : null;
					index[n.NodeId] = new NodeRef(b.Name, b.Kind, n.Key, id?.PhaseKey ?? n.Key, id?.WaveKey, id?.TaskKey, n.Name, n.Status, n.Type);
				}
		return index;
	}

	static LinkDto LinkRef(string nodeId, Dictionary<string, NodeRef> index) =>
		index.TryGetValue(nodeId, out var r)
			? new LinkDto(nodeId, r.Board, r.L1, r.L2, r.L3, r.Title, r.Status)
			: new LinkDto(nodeId, null, null, null, null, null, "missing");

	// COMPUTED spec roll-up: a spec node's delivery status derives from the tasks linked
	// (task_spec) to it AND its descendants.
	async Task<Dictionary<string, string>> ComputeSpecDeliveryAsync(string projectKey, IReadOnlyList<PlanNode> specNodes, CancellationToken ct)
	{
		var byNodeId = new Dictionary<string, (string Type, string Status)>(StringComparer.Ordinal);
		var ctx = _boards.GetContext(projectKey);
		foreach (var b in await _boards.ListAsync(projectKey, ct))
			foreach (var n in ctx.PlanNodes.Where(x => x.Board == b.Name && x.ActiveTo == null).ToList())
				if (n.NodeId.Length > 0) byNodeId[n.NodeId] = (n.Type, n.Status);

		var tasksOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var s in specNodes)
			tasksOf[s.NodeId] = (await _relations.ListAsync(projectKey, s.NodeId, "to", ct: ct))
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

	// ---- write: upsert ----

	public async Task<UpsertOutcome> UpsertAsync(string projectKey, string board, IReadOnlyList<NodePatch> nodes, long sinceVersion = 0, CancellationToken ct = default)
	{
		await _boards.EnsureAsync(projectKey, board, ct); // auto-vivify on first write
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		if (meta.ClosedAt != null)
			throw new InvalidOperationException($"board '{board}' is closed — reopen it (tasks.board_reopen) before writing");

		var kind = WorkflowCatalog.ParseKind(meta.Kind);
		var ctx = _boards.GetContext(projectKey);
		var prior = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList()
			.ToDictionary(n => n.Key, n => n, StringComparer.Ordinal);
		var desired = nodes.Select(p => ApplyWorkflow(kind, Merge(p, prior), prior) with { Board = board }).ToArray();
		ValidateChanges(desired, prior);
		var specRefs = LinkFields(nodes, p => p.SpecRef);
		var blockedBy = LinkFields(nodes, p => p.BlockedBy);
		RequireSpecLinks(kind, desired, prior, specRefs);
		await ValidateSpecRefsAsync(projectKey, meta, specRefs, ct);
		await RequireBlockersAsync(kind, projectKey, desired, blockedBy, ct);
		var r = await TemporalStore.UpsertAsync(ctx, desired, sinceVersion, partition: n => n.Board == board, ct: ct);
		if (r.Applied)
		{
			await _boards.TouchAsync(projectKey, board, ct);
			await LinkRefsAsync(projectKey, "task_spec", desired, specRefs, blockerIsFrom: false, ct);
			await LinkRefsAsync(projectKey, "blocks", desired, blockedBy, blockerIsFrom: true, ct);
			await CloseBlocksOnLeaveAsync(projectKey, desired, prior, ct);
			await SetTagsAsync(projectKey, board, nodes, desired, ct);
			await RunDoneEffectsAsync(projectKey, kind, desired, ct);
		}
		return new UpsertOutcome(r, kind);
	}

	public async Task<UpsertOutcome> DeltaAsync(string projectKey, string board, long sinceVersion, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		var ctx = _boards.GetContext(projectKey);
		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<PlanNode>(), sinceVersion, partition: n => n.Board == board, ct: ct);
		return new UpsertOutcome(r, WorkflowCatalog.ParseKind(meta.Kind));
	}

	// Read-merge a patch against the prior active row: a field omitted from the patch
	// (null) inherits the prior value; a non-null value sets it ("" clears it).
	static PlanNode Merge(NodePatch p, IReadOnlyDictionary<string, PlanNode> prior)
	{
		var cur = prior.GetValueOrDefault(p.Key) ?? (p.PrevKey is not null ? prior.GetValueOrDefault(p.PrevKey) : null);
		return new PlanNode
		{
			Key = p.Key,
			Version = p.Version,
			Status = p.Status ?? cur?.Status ?? string.Empty,
			Type = (p.Type ?? cur?.Type ?? string.Empty).ToLowerInvariant(),
			Name = p.Title ?? cur?.Name ?? string.Empty,
			Body = p.Body ?? cur?.Body ?? string.Empty,
			CommitRef = p.CommitRefSet ? p.CommitRef : cur?.CommitRef,
			Priority = p.Priority ?? cur?.Priority ?? 0,
			PrevKey = p.PrevKey,
		};
	}

	static Dictionary<string, string> LinkFields(IReadOnlyList<NodePatch> nodes, Func<NodePatch, string?> pick)
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var p in nodes)
		{
			var v = pick(p);
			if (!string.IsNullOrWhiteSpace(v)) map[p.Key] = v!;
		}
		return map;
	}

	// Default status, assign/carry the stable NodeId (new = fresh, edit = keep, rename =
	// inherit from source), and validate status/transition — the single workflow point.
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

	// Declarative immutable-field invariants (NodeId/type once set). The prior row is the
	// active node at this key, or — for a rename — at its PrevKey; null means a new node.
	static void ValidateChanges(PlanNode[] desired, Dictionary<string, PlanNode> prior)
	{
		foreach (var n in desired)
		{
			var old = prior.GetValueOrDefault(n.Key)
				?? (n.PrevKey is not null ? prior.GetValueOrDefault(n.PrevKey) : null);
			var result = ChangeValidator.Validate(new EntityChange<PlanNode>(old, n));
			if (!result.IsValid)
				throw new ArgumentException(result.Errors[0].ErrorMessage);
		}
	}

	// Validate each specRef target: it must resolve to a node on a spec board, and (if
	// this work board has a SpecBoard set) on that specific board.
	async Task ValidateSpecRefsAsync(string projectKey, TaskBoardMeta workBoard, Dictionary<string, string> specRefs, CancellationToken ct)
	{
		if (specRefs.Count == 0) return;
		var index = await BuildNodeIndexAsync(projectKey, ct);
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
	// issue that spawned it and unblock tasks it was blocking. System action (no gate).
	async Task RunDoneEffectsAsync(string projectKey, BoardKind kind, PlanNode[] desired, CancellationToken ct)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired.Where(n => WorkflowCatalog.KindOfSlug(n.Status) == StatusKind.TerminalOk))
		{
			foreach (var e in (await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Where(e => e.Kind == "issue_task"))
				await SetActiveNodeStatusAsync(projectKey, e.FromNodeId,
					(wf, node) => WorkflowCatalog.IsTerminalSlug(node.Status) ? null : wf?.Statuses.FirstOrDefault(s => s.Kind == StatusKind.TerminalOk)?.Slug, ct);

			foreach (var e in (await _relations.ListAsync(projectKey, n.NodeId, "from", ct: ct)).Where(e => e.Kind == "blocks"))
			{
				await _relations.CloseAsync(projectKey, "blocks", e.FromNodeId, e.ToNodeId, ct);
				var stillBlocked = (await _relations.ListAsync(projectKey, e.ToNodeId, "to", ct: ct)).Any(x => x.Kind == "blocks");
				if (!stillBlocked)
					await SetActiveNodeStatusAsync(projectKey, e.ToNodeId,
						(_, node) => string.Equals(node.Status, "Blocked", StringComparison.OrdinalIgnoreCase) ? "InProgress" : null, ct);
			}
		}
	}

	// Find the active node with this NodeId across the project's boards and move it to a
	// target status chosen by `pick` (null = leave as-is). System action (no gate).
	async Task SetActiveNodeStatusAsync(string projectKey, string nodeId, Func<PetBox.Tasks.Workflow.Workflow?, PlanNode, string?> pick, CancellationToken ct)
	{
		// NodeId is unique across the project, so find the active row directly in the one
		// project file; its Board tells us which partition to write back into.
		var ctx = _boards.GetContext(projectKey);
		var node = ctx.PlanNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault();
		if (node is null) return;
		var meta = await _boards.FindAsync(projectKey, node.Board, ct);
		var wf = WorkflowCatalog.For(WorkflowCatalog.ParseKind(meta?.Kind), node.Type.Length == 0 ? null : node.Type);
		var target = pick(wf, node);
		if (target is null || string.Equals(target, node.Status, StringComparison.OrdinalIgnoreCase)) return;
		await TemporalStore.UpsertAsync(ctx, new[] { node with { Status = target } }, partition: n => n.Board == node.Board, ct: ct);
		await _boards.TouchAsync(projectKey, node.Board, ct);
	}

	// Create relation edges from a per-node field after the upsert applies. task_spec
	// (specRef): task -> spec. blocks (blockedBy): blocker -> task. Idempotent.
	async Task LinkRefsAsync(string projectKey, string kind, PlanNode[] desired, Dictionary<string, string> refs, bool blockerIsFrom, CancellationToken ct)
	{
		if (refs.Count == 0) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (key, other) in refs)
			if (byKey.TryGetValue(key, out var nid) && nid.Length > 0)
			{
				var (from, to) = blockerIsFrom ? (other, nid) : (nid, other);
				await _relations.CreateAsync(projectKey, kind, from, to, ct);
			}
	}

	// Apply enforced tags after the upsert. A patch whose Tags is null OMITS tags (leave
	// as-is); a non-null list (incl. empty) is the new full set for that node. Tags bind
	// to the node's stable NodeId.
	async Task SetTagsAsync(string projectKey, string board, IReadOnlyList<NodePatch> patches, PlanNode[] desired, CancellationToken ct)
	{
		var nodeIdOf = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var p in patches)
		{
			if (p.Tags is null) continue;
			if (nodeIdOf.TryGetValue(p.Key, out var nid) && nid.Length > 0)
				await _tags.SetAsync(projectKey, board, nid, p.Tags, ct);
		}
	}

	// Invariant: a work task in `Blocked` must name a blocker (blockedBy in this call, or
	// an already-active `blocks` edge into it). "Blocked requires a link."
	async Task RequireBlockersAsync(BoardKind kind, string projectKey, PlanNode[] desired, Dictionary<string, string> blockedBy, CancellationToken ct)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired)
		{
			if (!string.Equals(n.Status, "Blocked", StringComparison.OrdinalIgnoreCase)) continue;
			if (blockedBy.ContainsKey(n.Key)) continue;
			var hasActiveBlocker = (await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Any(e => e.Kind == "blocks");
			if (!hasActiveBlocker)
				throw new ArgumentException($"a Blocked task must name a blocker — provide blockedBy (node '{n.Key}')");
		}
	}

	// Leaving Blocked manually closes the active `blocks` edges into the node (history kept).
	async Task CloseBlocksOnLeaveAsync(string projectKey, PlanNode[] desired, Dictionary<string, PlanNode> prior, CancellationToken ct)
	{
		foreach (var n in desired)
		{
			var wasBlocked = prior.TryGetValue(n.Key, out var cur) && string.Equals(cur.Status, "Blocked", StringComparison.OrdinalIgnoreCase);
			if (!wasBlocked || string.Equals(n.Status, "Blocked", StringComparison.OrdinalIgnoreCase)) continue;
			foreach (var e in (await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Where(e => e.Kind == "blocks"))
				await _relations.CloseAsync(projectKey, "blocks", e.FromNodeId, e.ToNodeId, ct);
		}
	}

	// Invariant: a NEW work feature/bug must link a spec node (specRef). Edits don't re-require it.
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

	// Active node key -> chain of prior keys it was renamed from.
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

	// ---- UI quick-add ----

	public async Task QuickAddAsync(string projectKey, string board, string name, string? body, long priority, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(name)) return;

		// Status/type must fit the board's kind — a cold "Pending" is invalid on kinded
		// boards (ideas/spec/intake). Use the kind's initial status + its node type.
		var meta = await _boards.FindAsync(projectKey, board, ct);
		var kind = WorkflowCatalog.ParseKind(meta?.Kind);
		var type = kind switch
		{
			BoardKind.Ideas => "idea",
			BoardKind.Spec => "spec",
			BoardKind.Intake => "issue",
			_ => "",
		};
		var status = WorkflowCatalog.For(kind, type.Length == 0 ? null : type)?.Initial ?? "Pending";

		var key = new TaskNodeId("incoming", GenKey(name), null).ToKey();
		var ctx = _boards.GetContext(projectKey);
		// Assign a stable NodeId so the node can be linked (relations/specRef) later.
		await TemporalStore.UpsertAsync(ctx, new[]
		{
			new PlanNode { Board = board, Key = key, NodeId = Guid.NewGuid().ToString("N"), Version = 0, Status = status, Type = type, Name = name.Trim(), Body = body?.Trim() ?? string.Empty, Priority = priority },
		}, partition: n => n.Board == board, ct: ct);
		await _boards.TouchAsync(projectKey, board, ct);
	}

	static string GenKey(string name)
	{
		var ascii = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
		if (ascii.Length == 0 || !char.IsLetter(ascii[0])) ascii = "task-" + ascii;
		ascii = ascii.Trim('-');
		if (ascii.Length > 32) ascii = ascii[..32].Trim('-');
		return $"{ascii}-{Guid.NewGuid():N}"[..(Math.Min(ascii.Length, 32) + 7)];
	}

	// ---- system: report.issue ----

	public async Task<string> ReportIssueAsync(string project, string board, string title, string body, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required");
		var key = new TaskNodeId("incoming", IssueSlug(title), null).ToKey();
		await _boards.EnsureAsync(project, board, ct); // auto-create the triage board on first report
		var ctx = _boards.GetContext(project);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new PlanNode { Board = board, Key = key, Version = 0, Status = "reported", Type = "issue", Name = title.Trim(), Body = body, Priority = 50 },
		}, partition: n => n.Board == board, ct: ct);
		if (r.Applied) await _boards.TouchAsync(project, board, ct);
		return key;
	}

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex NonSlug();

	static string IssueSlug(string title)
	{
		var s = NonSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
		if (s.Length == 0 || !char.IsLetter(s[0])) s = "issue-" + s;
		if (s.Length > 32) s = s[..32].Trim('-');
		return $"{s}-{Guid.NewGuid():N}"[..(s.Length + 1 + 6)];
	}
}
