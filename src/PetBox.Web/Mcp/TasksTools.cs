using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data.Temporal;
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
	[Description("Create a named task board in a project. `kind` sets the board role (free|spec|ideas|intake|work; default free) which drives the workflow — see tasks.workflow. Requires tasks:write.")]
	public static async Task<object> BoardCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, string? kind = null, string? description = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var meta = await boards.CreateAsync(projectKey, board, description, kind ?? "free", ct);
		return new { meta.ProjectKey, meta.Name, meta.Kind, meta.Description, meta.CreatedAt };
	}

	[McpServerTool(Name = "tasks.board_list", Title = "List task boards", ReadOnly = true)]
	[Description("List task boards in a project. Requires tasks:read.")]
	public static async Task<object> BoardListAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await boards.ListAsync(projectKey, ct);
		return new { boards = list.Select(b => new { b.Name, b.Kind, b.Description, b.CreatedAt }).ToList() };
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

	[McpServerTool(Name = "tasks.get", Title = "Get a board's nodes", ReadOnly = true)]
	[Description("Return the active plan nodes of a board as a 1-to-3 level tree, ordered by priority then path. Each node carries key, nodeId, l1, l2, l3, depth, parentKey, status, type, title, body, priority, version, renamedFrom. On a spec board each node also carries `delivery` — the COMPUTED roll-up from linked tasks (not_started|in_progress|done|done_with_defects). Requires tasks:read.")]
	public static async Task<object> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards, IRelationStore relations,
		string projectKey, string board, CancellationToken ct = default)
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

		var kind = WorkflowCatalog.ParseKind(await boards.KindAsync(projectKey, board, ct));
		var delivery = kind == BoardKind.Spec ? await ComputeSpecDeliveryAsync(boards, relations, projectKey, active, ct) : null;
		return new
		{
			currentVersion = current,
			nodes = active.Select(n =>
			{
				var id = TaskNodeId.TryParse(n.Key, out var pid) ? pid : null;
				return new
				{
					key = n.Key,
					nodeId = n.NodeId,
					l1 = id?.PhaseKey ?? n.Key,
					l2 = id?.WaveKey,
					l3 = id?.TaskKey,
					depth = id?.Depth ?? 1,
					parentKey = id?.ParentKey,
					status = n.Status,
					type = n.Type,
					title = n.Name,
					body = n.Body,
					commitRef = n.CommitRef,
					priority = n.Priority,
					version = n.Version,
					delivery = delivery is not null && delivery.TryGetValue(n.Key, out var dv) ? dv : null,
					renamedFrom = lineage.TryGetValue(n.Key, out var p) ? p : [],
				};
			}).ToList(),
		};
	}

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

		var kind = WorkflowCatalog.ParseKind(await boards.KindAsync(projectKey, board, ct));
		var ctx = boards.GetContext(projectKey, board);
		var prior = ctx.PlanNodes.Where(n => n.ActiveTo == null).ToList()
			.ToDictionary(n => n.Key, n => n, StringComparer.Ordinal);
		var desired = ParseNodes(nodes).Select(n => ApplyWorkflow(kind, n, prior)).ToArray();
		var specRefs = ParseNodeField(nodes, "specRef");
		var blockedBy = ParseNodeField(nodes, "blockedBy");
		RequireSpecLinks(kind, desired, prior, specRefs);
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
		return Serialize(r);
	});

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

		var ctx = boards.GetContext(projectKey, board);
		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<PlanNode>(), sinceVersion, ct: ct);
		return Serialize(r);
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

		var kind = WorkflowCatalog.ParseKind(await boards.KindAsync(projectKey, board, ct));
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

	static object Serialize(TemporalUpsertResult<PlanNode> r) => new
	{
		applied = r.Applied,
		currentVersion = r.CurrentVersion,
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

	static object NodeDto(PlanNode n)
	{
		var id = TaskNodeId.TryParse(n.Key, out var p) ? p : null;
		return new
		{
			key = n.Key,
			nodeId = n.NodeId,
			l1 = id?.PhaseKey ?? n.Key,
			l2 = id?.WaveKey,
			l3 = id?.TaskKey,
			depth = id?.Depth ?? 1,
			parentKey = id?.ParentKey,
			status = n.Status,
			type = n.Type,
			title = n.Name,
			body = n.Body,
			commitRef = n.CommitRef,
			priority = n.Priority,
			version = n.Version,
		};
	}

	static PlanNode[] ParseNodes(JsonElement nodes)
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
			list.Add(new PlanNode
			{
				Key = ResolveKey(e),
				Version = ModuleMcp.OptLong(e, "version", 0),
				Status = ModuleMcp.OptStr(e, "status") ?? string.Empty,
				Type = (ModuleMcp.OptStr(e, "type") ?? string.Empty).ToLowerInvariant(),
				Name = ModuleMcp.OptStr(e, "title") ?? string.Empty,
				Body = ModuleMcp.OptStr(e, "body") ?? string.Empty,
				CommitRef = ModuleMcp.OptStr(e, "commitRef"),
				Priority = ModuleMcp.OptLong(e, "priority", 0),
				PrevKey = ResolvePrevKey(e),
			});
		}
		return list.ToArray();
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
