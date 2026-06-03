using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Web.Mcp;

// MCP surface for the Tasks module: named board lifecycle + temporal node content.
// This is a THIN adapter — it asserts the scope/feature/project guards, parses the
// JSON node payload into typed NodePatch, and delegates every domain decision to
// ITasksService (the single door to the task store). It must not touch the store or
// DB context directly (a NetArchTest enforces this). Scopes: tasks:read / tasks:write.
[McpServerToolType]
public static class TasksTools
{
	[McpServerTool(Name = "tasks.board_create", Title = "Create a task board")]
	[Description("Create a named task board in a project. `kind` sets the board role (free|spec|ideas|intake|work; default free) which drives the workflow — see tasks.workflow. `specBoard` (work boards only) names the spec board this board's tasks link into, so specRef targets are validated against it and the agent need not guess. Requires tasks:write.")]
	public static async Task<object> BoardCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, string? kind = null, string? description = null, string? specBoard = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var meta = await tasks.CreateBoardAsync(projectKey, board, kind, description, specBoard, ct);
		return new { meta.ProjectKey, meta.Name, meta.Kind, meta.Description, meta.SpecBoard, meta.CreatedAt };
	}

	[McpServerTool(Name = "tasks.board_set_spec", Title = "Set a work board's spec board")]
	[Description("Set (or clear, when specBoard is omitted) the spec board a work board's tasks link into. The target must be a spec board. Makes the work->spec link explicit. Requires tasks:write.")]
	public static async Task<object> BoardSetSpecAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, string? specBoard = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var (set, norm) = await tasks.SetSpecBoardAsync(projectKey, board, specBoard, ct);
		return new { set, specBoard = norm };
	}

	[McpServerTool(Name = "tasks.board_list", Title = "List task boards", ReadOnly = true)]
	[Description("List task boards in a project, each with its kind, specBoard (work->spec link, if set) and closed flag. Requires tasks:read.")]
	public static async Task<object> BoardListAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await tasks.ListBoardsAsync(projectKey, ct);
		return new { boards = list.Select(b => new { b.Name, b.Kind, b.Description, b.SpecBoard, b.CreatedAt, closed = b.ClosedAt != null }).ToList() };
	}

	[McpServerTool(Name = "tasks.board_delete", Title = "Delete a task board", Destructive = true)]
	[Description("Delete a task board and its nodes. Requires tasks:write.")]
	public static async Task<object> BoardDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new { deleted = await tasks.DeleteBoardAsync(projectKey, board, ct) };
	}

	[McpServerTool(Name = "tasks.board_close", Title = "Close (archive) a task board")]
	[Description("Close a board: it rejects further writes (so agents stop writing to it by inertia) but stays readable; history is kept. Reopen with tasks.board_reopen. Requires tasks:write.")]
	public static async Task<object> BoardCloseAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new { closed = await tasks.SetClosedAsync(projectKey, board, true, ct) };
	}

	[McpServerTool(Name = "tasks.board_reopen", Title = "Reopen a closed task board")]
	[Description("Reopen a closed board so it accepts writes again. Requires tasks:write.")]
	public static async Task<object> BoardReopenAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new { reopened = await tasks.SetClosedAsync(projectKey, board, false, ct) };
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
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, bool includeClosed = false, string? under = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		return await tasks.GetAsync(projectKey, board, includeClosed, under, ct);
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
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board,
		[Description("JSON array of node objects. A node may carry specRef (a spec NodeId) — on a work board this links the task to that spec node (task_spec edge).")] JsonElement nodes,
		long sinceVersion = 0, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var patches = ParseNodePatches(nodes);
		return Serialize(await tasks.UpsertAsync(projectKey, board, patches, sinceVersion, ct));
	});

	[McpServerTool(Name = "tasks.delta", Title = "Plan delta since cursor", ReadOnly = true)]
	[Description("Return nodes added/updated/removed since `sinceVersion` (no writes). Requires tasks:read.")]
	public static async Task<object> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, long sinceVersion, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		return Serialize(await tasks.DeltaAsync(projectKey, board, sinceVersion, ct));
	}

	[McpServerTool(Name = "tasks.workflow", Title = "Board workflow (kinds/statuses/transitions)", ReadOnly = true)]
	[Description("Return the workflow for a board: its kind and the task types it hosts, each with statuses (slug, name, kind=open|terminalok|terminalcancel), the initial status, and transitions (from, to, requiresApproval, requiresReason). Use this to learn the legal statuses before tasks.upsert. Requires tasks:read.")]
	public static async Task<object> WorkflowAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var kind = await tasks.ResolveKindAsync(projectKey, board, ct);
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

	// ---- adapter plumbing: JSON parsing + wire shaping (no domain logic) ----

	static object Serialize(UpsertOutcome o)
	{
		var r = o.Result;
		return new
		{
			applied = r.Applied,
			currentVersion = r.CurrentVersion,
			kind = o.Kind.ToString().ToLowerInvariant(),
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
	}

	// Delta projection of a node (no links/delivery — that's tasks.get). camelCased by the serializer.
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

	// Parse the node array into typed patches. Read-merge (inheriting omitted fields from
	// the prior row) happens in the service; here a field absent from the JSON maps to
	// null (inherit) and a present field to its value ("" = explicit clear). MCP clients
	// sometimes pass the array as a JSON *string*, so accept both forms.
	static List<NodePatch> ParseNodePatches(JsonElement nodes)
	{
		using var doc = nodes.ValueKind == JsonValueKind.String
			? JsonDocument.Parse(nodes.GetString() ?? "")
			: (JsonDocument?)null;
		var arr = doc?.RootElement ?? nodes;
		if (arr.ValueKind != JsonValueKind.Array)
			throw new ArgumentException($"nodes must be a JSON array (got {arr.ValueKind})");
		var list = new List<NodePatch>();
		foreach (var e in arr.EnumerateArray())
		{
			list.Add(new NodePatch
			{
				Key = ResolveKey(e),
				PrevKey = ResolvePrevKey(e),
				Version = ModuleMcp.OptLong(e, "version", 0),
				Status = Has(e, "status") ? ModuleMcp.OptStr(e, "status") ?? string.Empty : null,
				Type = Has(e, "type") ? ModuleMcp.OptStr(e, "type") ?? string.Empty : null,
				Title = Has(e, "title") ? ModuleMcp.OptStr(e, "title") ?? string.Empty : null,
				Body = Has(e, "body") ? ModuleMcp.OptStr(e, "body") ?? string.Empty : null,
				CommitRefSet = Has(e, "commitRef"),
				CommitRef = Has(e, "commitRef") ? ModuleMcp.OptStr(e, "commitRef") : null,
				Priority = Has(e, "priority") ? ModuleMcp.OptLong(e, "priority", 0) : null,
				SpecRef = ModuleMcp.OptStr(e, "specRef"),
				BlockedBy = ModuleMcp.OptStr(e, "blockedBy"),
				Tags = ParseTags(e),
			});
		}
		return list;

		static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);
	}

	// Enforced tags. Absent → null (omit, inherit). Present → the full replacement set:
	// a JSON array of strings, a double-encoded JSON-string array (some MCP clients), or a
	// CSV string. JSON null or [] → empty set (clears the node's tags).
	static IReadOnlyList<string>? ParseTags(JsonElement e)
	{
		if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("tags", out var t)) return null;
		switch (t.ValueKind)
		{
			case JsonValueKind.Null:
				return [];
			case JsonValueKind.Array:
				return t.EnumerateArray()
					.Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText())
					.Where(s => s.Length > 0).ToList();
			case JsonValueKind.String:
				var s = t.GetString() ?? "";
				if (s.TrimStart().StartsWith('['))
				{
					try
					{
						using var d = JsonDocument.Parse(s);
						return d.RootElement.EnumerateArray().Select(x => x.GetString() ?? "").Where(v => v.Length > 0).ToList();
					}
					catch { /* not JSON — fall through to CSV */ }
				}
				return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			default:
				return null;
		}
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
}
