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
	[Description("Return the active plan nodes of a board as a Phase>Wave>Task tree, ordered by priority then path. Each node carries key, phase, wave, task, depth, parentKey, status, name, body, priority, version, renamedFrom. Requires tasks:read.")]
	public static async Task<object> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
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
					phase = id?.PhaseKey ?? n.Key,
					wave = id?.WaveKey,
					task = id?.TaskKey,
					depth = id?.Depth ?? 1,
					parentKey = id?.ParentKey,
					status = n.Status,
					type = n.Type,
					name = n.Name,
					body = n.Body,
					commitRef = n.CommitRef,
					priority = n.Priority,
					version = n.Version,
					renamedFrom = lineage.TryGetValue(n.Key, out var p) ? p : [],
				};
			}).ToList(),
		};
	}

	[McpServerTool(Name = "tasks.upsert", Title = "Upsert plan nodes")]
	[Description("""
		Declarative temporal upsert of plan nodes. Requires tasks:write.

		A plan is a 1-to-3 level tree: Phase > Wave > Task. Identify each node by path —
		phase (required), optional wave, optional task (needs wave), or a "phase/wave/task"
		string in `key`. Segments are lowercase [a-z][a-z0-9_-]{0,99}. Create parents
		with/before children. Give each node a short `name` (title) and a `body` (markdown
		detail). Other fields: status (Pending|InProgress|Done|Blocked|Deferred|Cancelled),
		commitRef?, priority? (sparse int, lower first), version (baseline you last saw; 0 =
		new). Rename via prevPhase/prevWave/prevTask or prevKey. A cold call auto-creates the board.

		Returns { applied, currentVersion, inserted, closed, conflicts[], added[], updated[],
		removed[] }; added/updated carry the full node (key, phase, wave, task, depth,
		parentKey, status, name, body, commitRef, priority, version). The delta IS the fresh
		state since `sinceVersion` — advance your cursor and merge, no need to re-read.
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
		RequireSpecLinks(kind, desired, prior, ParseSpecRefs(nodes));
		var r = await TemporalStore.UpsertAsync(ctx, desired, sinceVersion, ct: ct);
		if (r.Applied)
		{
			await boards.TouchAsync(projectKey, board, ct);
			await LinkSpecRefsAsync(relations, projectKey, nodes, desired, ct);
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
			var edges = await relations.ListAsync(projectKey, n.NodeId, "to", ct);
			foreach (var e in edges.Where(e => e.Kind == "issue_task"))
				await CloseLinkedNodeAsync(boards, projectKey, e.FromNodeId, ct);
		}
	}

	// Find the active node with this NodeId across the project's boards and move it to
	// its workflow's TerminalOk status (e.g. intake issue -> done). No-op if missing/terminal.
	static async Task CloseLinkedNodeAsync(ITaskBoardStore boards, string projectKey, string nodeId, CancellationToken ct)
	{
		foreach (var b in await boards.ListAsync(projectKey, ct))
		{
			var ctx = boards.GetContext(projectKey, b.Name);
			var node = ctx.PlanNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault();
			if (node is null) continue;
			if (WorkflowCatalog.IsTerminalSlug(node.Status)) return; // already closed
			var wf = WorkflowCatalog.For(WorkflowCatalog.ParseKind(b.Kind), node.Type.Length == 0 ? null : node.Type);
			var doneSlug = wf?.Statuses.FirstOrDefault(s => s.Kind == StatusKind.TerminalOk)?.Slug;
			if (doneSlug is null) return;
			await TemporalStore.UpsertAsync(ctx, new[] { node with { Status = doneSlug } }, ct: ct);
			await boards.TouchAsync(projectKey, b.Name, ct);
			return;
		}
	}

	// specRef on a node = "this task implements that spec node" → create a task_spec
	// edge (task NodeId -> spec NodeId) after the upsert applies. Idempotent.
	static async Task LinkSpecRefsAsync(IRelationStore relations, string projectKey, JsonElement nodes, PlanNode[] desired, CancellationToken ct)
	{
		var specRefs = ParseSpecRefs(nodes);
		if (specRefs.Count == 0) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (key, specRef) in specRefs)
			if (byKey.TryGetValue(key, out var nid) && nid.Length > 0)
				await relations.CreateAsync(projectKey, "task_spec", nid, specRef, ct);
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

	static Dictionary<string, string> ParseSpecRefs(JsonElement nodes)
	{
		using var doc = nodes.ValueKind == JsonValueKind.String
			? JsonDocument.Parse(nodes.GetString() ?? "")
			: (JsonDocument?)null;
		var arr = doc?.RootElement ?? nodes;
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		if (arr.ValueKind != JsonValueKind.Array) return map;
		foreach (var e in arr.EnumerateArray())
		{
			var sr = ModuleMcp.OptStr(e, "specRef");
			if (!string.IsNullOrWhiteSpace(sr)) map[ResolveKey(e)] = sr!;
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
			phase = id?.PhaseKey ?? n.Key,
			wave = id?.WaveKey,
			task = id?.TaskKey,
			depth = id?.Depth ?? 1,
			parentKey = id?.ParentKey,
			status = n.Status,
			type = n.Type,
			name = n.Name,
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
				Name = ModuleMcp.OptStr(e, "name") ?? string.Empty,
				Body = ModuleMcp.OptStr(e, "body") ?? string.Empty,
				CommitRef = ModuleMcp.OptStr(e, "commitRef"),
				Priority = ModuleMcp.OptLong(e, "priority", 0),
				PrevKey = ResolvePrevKey(e),
			});
		}
		return list.ToArray();
	}

	// A node's identity is the Phase/Wave/Task path. Preferred input is the
	// structured form (phase + optional wave + optional task); a canonical
	// "phase/wave/task" string in `key` is accepted as an alternative. Both are
	// validated and canonicalised through TaskNodeId.
	static string ResolveKey(JsonElement e)
	{
		var phase = ModuleMcp.OptStr(e, "phase");
		if (phase is not null)
			return new TaskNodeId(phase, ModuleMcp.OptStr(e, "wave"), ModuleMcp.OptStr(e, "task")).ToKey();

		var key = ModuleMcp.OptStr(e, "key");
		if (key is not null)
			return TaskNodeId.Parse(key).ToKey();

		throw new ArgumentException("each node needs 'phase' (+optional wave/task) or a 'key' path");
	}

	static string? ResolvePrevKey(JsonElement e)
	{
		var prevPhase = ModuleMcp.OptStr(e, "prevPhase");
		if (prevPhase is not null)
			return new TaskNodeId(prevPhase, ModuleMcp.OptStr(e, "prevWave"), ModuleMcp.OptStr(e, "prevTask")).ToKey();

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
