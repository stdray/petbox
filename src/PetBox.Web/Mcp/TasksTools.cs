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

	[McpServerTool(Name = "tasks.methodology_enable", Title = "Enable the methodology quartet")]
	[Description("Provision the four singleton methodology boards (intake/ideas/spec/work) if missing and auto-wire work->spec. Idempotent — opt-in; a project's methodology lives on these, ad-hoc work stays on free boards. The four kinds are one-per-project. Requires tasks:write. Returns the quartet surface (intake→ideas→spec→work).")]
	public static async Task<object> MethodologyEnableAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return (object)await tasks.EnableMethodologyAsync(projectKey, ct);
	});

	[McpServerTool(Name = "tasks.methodology_get", Title = "Get the methodology quartet", ReadOnly = true)]
	[Description("""
		Return the project's methodology quartet as ONE compact INDEX in pipeline order:
		intake → ideas → spec → work. Each board carries a status histogram (`counts`: status
		slug -> active-node count) and its active nodes as INDEX rows — `key`, `nodeId`,
		`parentSlug`/`depth` (part_of nav), `status`, `type`, `title`, `priority`, `tags`
		(ALWAYS), links (`spec`/`blockedBy`/`linkedTasks`/`supersedes`) and the computed
		`delivery` roll-up — but NO `body` by default (this is the orientation index, not a
		dump; null fields are omitted). Pass `bodyLen` > 0 to include the first N chars of
		each body (a snippet — the key line(s) without detail; "…" appended when cut; a large
		N ≈ the full body). Pass `includeBoards` (e.g. ["spec","ideas"]) to return only those
		quartet boards (kinds: intake|ideas|spec|work). For full untruncated bodies or
		subtree drill-down, use tasks.get (the single-board detail endpoint). `enabled` is
		true when all four singleton boards exist. Requires tasks:read.
		""")]
	public static async Task<object> MethodologyGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Slice length (chars) of each node body to include; 0 = index only (no bodies). \"…\" is appended when a body is cut.")] int bodyLen = 0,
		[Description("Restrict to these quartet boards by kind (intake|ideas|spec|work); empty/omitted = all four.")] string[]? includeBoards = null,
		[Description("Include an absolute `url` permalink to each node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return (object)await tasks.GetMethodologyAsync(projectKey, bodyLen, includeBoards, urlPrefix, ct);
	});

	[McpServerTool(Name = "tasks.get", Title = "Get a board's nodes", ReadOnly = true)]
	[Description("""
		Return the active plan nodes of a board, ordered by priority then key. Nodes are FLAT
		(a single slug `key`); hierarchy is the `part_of` edge, surfaced as parentNodeId,
		parentSlug and a computed `depth` (0 = root) — build the tree from those. Top level
		carries `kind` (the board role) and `specBoard`. Each node carries key, nodeId,
		parentNodeId, parentSlug, depth, status, type, title, body, priority, version,
		renamedFrom, `tags` (enforced area:*/concern:* tags), plus links: `spec` (spec nodes
		this task implements — task_spec), `blockedBy` (nodes blocking it), and on a spec board
		`linkedTasks` plus the COMPUTED `delivery` roll-up (not_started|in_progress|done|
		done_with_defects), rolled up over the part_of subtree. By default terminal/closed
		nodes are HIDDEN — pass includeClosed=true; closed part_of ancestors of a visible node
		are kept so the tree stays connected. `under` (a node slug) restricts to that part_of
		subtree. Pass `groupBy` (area|concern) instead to get the tag PROJECTION: nodes
		bucketed by their tag value in that namespace ("(none)" for untagged), each group
		with a delivery roll-up — the cross-cutting view a single-parent tree can't give.
		Requires tasks:read.
		""")]
	public static async Task<object> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, bool includeClosed = false, string? under = null, string? groupBy = null,
		[Description("Include an absolute `url` permalink to each node's detail page (off by default; ignored with groupBy).")] bool includeUrl = false,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		return string.IsNullOrWhiteSpace(groupBy)
			? (object)await tasks.GetAsync(projectKey, board, includeClosed, under, await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct), ct)
			: await tasks.GetGroupedAsync(projectKey, board, groupBy, ct);
	});

	[McpServerTool(Name = "tasks.upsert", Title = "Upsert plan nodes")]
	[Description("""
		Declarative temporal upsert of plan nodes. Requires tasks:write.

		Each node has a FLAT `key` — a single slug [a-z][a-z0-9_-]{0,99} (no '/'; the old
		l1/l2/l3 path is gone). Nesting is the `partOf` field: a parent slug (on this board)
		or a NodeId — null omits it, "" detaches to a root. A node may carry multiple parents'
		worth of grouping via `tags` (an array of "namespace:value", namespaces area|concern;
		[] clears, omit leaves as-is). Give each node a `title` and `body` (markdown). Other
		fields: status (slug — see tasks.workflow), type (feature|bug on work boards), specRef
		(a spec NodeId the work task implements), ideaRef (ON A SPEC BOARD: the NodeId of the
		`accepted` idea this create/change is made under — REQUIRED for every spec node; becomes
		the idea_spec edge), blockedBy (a NodeId blocking it), supersedes
		(a slug|NodeId this node replaces — the old one is moved to its terminal-cancel),
		commitRef?, priority? (sparse int, lower first), version (baseline you last saw; 0 =
		new). Rename via prevKey. A cold call auto-creates the board.

		Returns { applied, currentVersion, inserted, closed, conflicts[], added[], updated[],
		removed[] }; added/updated carry the node (key, nodeId, status, type, title, body,
		commitRef, priority, version). The delta IS the fresh state since `sinceVersion` —
		advance your cursor and merge, no need to re-read.
		""")]
	public static async Task<object> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board,
		[Description("JSON array of node objects: flat `key`, optional `partOf` (parent slug|NodeId), `tags` (array of ns:value), `specRef`, `blockedBy`, status/type/title/body/priority/version.")] JsonElement nodes,
		long sinceVersion = 0,
		[Description("Include an absolute `url` permalink to each returned node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var patches = ParseNodePatches(nodes);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.UpsertAsync(projectKey, board, patches, sinceVersion, ct), urlPrefix);
	});

	[McpServerTool(Name = "tasks.delta", Title = "Plan delta since cursor", ReadOnly = true)]
	[Description("Return nodes added/updated/removed since `sinceVersion` (no writes). Requires tasks:read.")]
	public static async Task<object> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, long sinceVersion,
		[Description("Include an absolute `url` permalink to each returned node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.DeltaAsync(projectKey, board, sinceVersion, ct), urlPrefix);
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

	// Build the absolute permalink prefix for this project's nodes
	// ("{scheme}://{host}/ui/{ws}/{project}/tasks/node/"), or null when include_url is off or
	// the workspace can't be resolved. Per-node url = prefix + nodeId; scheme/host come from
	// the request (honor forwarded headers behind a proxy).
	static async Task<string?> UrlPrefixAsync(IHttpContextAccessor http, ITasksService tasks, string projectKey, bool includeUrl, CancellationToken ct)
	{
		if (!includeUrl) return null;
		var req = http.HttpContext?.Request;
		if (req is null) return null;
		var ws = await tasks.ResolveWorkspaceAsync(projectKey, ct);
		if (string.IsNullOrEmpty(ws)) return null;
		return $"{req.Scheme}://{req.Host}{Routes.TaskBoardNode(ws, projectKey, string.Empty)}";
	}

	static UpsertResultView Serialize(UpsertOutcome o, string? urlPrefix = null)
	{
		var r = o.Result;
		return new UpsertResultView(
			Applied: r.Applied,
			CurrentVersion: r.CurrentVersion,
			Kind: o.Kind.ToString().ToLowerInvariant(),
			Inserted: r.Inserted,
			Closed: r.Closed,
			Conflicts: r.Conflicts.Select(c => new UpsertConflictView(c.Key, c.Kind.ToString(), c.BaselineVersion, c.ActiveVersion)).ToList(),
			Added: r.Added.Select(n => NodeDto(n, urlPrefix)).ToList(),
			Updated: r.Updated.Select(n => NodeDto(n, urlPrefix)).ToList(),
			Removed: r.Removed.ToList());
	}

	// Delta projection of a node (no links/delivery/tags — that's tasks.get). camelCased by the serializer.
	static PlanNodeDelta NodeDto(PlanNode n, string? urlPrefix = null) => new(
		Key: n.Key,
		NodeId: n.NodeId,
		Status: n.Status,
		Type: n.Type,
		Title: n.Name,
		Body: n.Body,
		CommitRef: n.CommitRef,
		Priority: n.Priority,
		Version: n.Version,
		Url: urlPrefix is null ? null : urlPrefix + n.NodeId);

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
				IdeaRef = ModuleMcp.OptStr(e, "ideaRef"),
				BlockedBy = ModuleMcp.OptStr(e, "blockedBy"),
				PartOf = Has(e, "partOf") ? ModuleMcp.OptStr(e, "partOf") ?? string.Empty : null,
				Supersedes = ModuleMcp.OptStr(e, "supersedes"),
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

	// A node's address is a flat board-unique slug in `key` (`l1` accepted as an alias).
	// Nesting is the `partOf` parent, not the key. Validated/normalized via TaskSlug.
	static string ResolveKey(JsonElement e)
	{
		var key = ModuleMcp.OptStr(e, "key") ?? ModuleMcp.OptStr(e, "l1");
		if (key is not null)
			return TaskSlug.Validate(key);
		throw new ArgumentException("each node needs a 'key' (a flat slug)");
	}

	static string? ResolvePrevKey(JsonElement e)
	{
		var prevKey = ModuleMcp.OptStr(e, "prevKey") ?? ModuleMcp.OptStr(e, "prevL1");
		return prevKey is not null ? TaskSlug.Validate(prevKey) : null;
	}
}
