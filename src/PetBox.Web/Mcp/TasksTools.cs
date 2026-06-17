using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for the Tasks module: named board lifecycle + temporal node content.
// This is a THIN adapter — it asserts the scope/feature/project guards, parses the
// JSON node payload into typed NodePatch, and delegates every domain decision to
// ITasksService (the single door to the task store). It must not touch the store or
// DB context directly (a NetArchTest enforces this). Scopes: tasks:read / tasks:write.
[McpServerToolType]
public static class TasksTools
{
	[McpServerTool(Name = "tasks.board_create", Title = "Create a task board", UseStructuredContent = true)]
	[Description("Create a named task board in a project. `kind` sets the board role (simple|spec|ideas|intake|work; default simple) which drives the workflow — call tasks.workflow to see the valid types/statuses/transitions for a kind. `specBoard` (work boards only) names the spec board this board's tasks link into, so specRef targets are validated against it and the agent need not guess. Requires tasks:write.")]
	public static async Task<BoardCreatedResult> BoardCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, string? kind = null, string? description = null, string? specBoard = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var meta = await tasks.CreateBoardAsync(projectKey, board, kind, description, specBoard, ct);
		return new BoardCreatedResult(meta.ProjectKey, meta.Name, meta.Kind, meta.Description, meta.SpecBoard, meta.CreatedAt);
	}

	[McpServerTool(Name = "tasks.board_set_spec", Title = "Set a work board's spec board", UseStructuredContent = true)]
	[Description("Set (or clear, when specBoard is omitted) the spec board a work board's tasks link into. The target must be a spec board. Makes the work->spec link explicit. Requires tasks:write.")]
	public static async Task<BoardSetSpecResult> BoardSetSpecAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, string? specBoard = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var (set, norm) = await tasks.SetSpecBoardAsync(projectKey, board, specBoard, ct);
		return new BoardSetSpecResult(set, norm);
	}

	[McpServerTool(Name = "tasks.board_list", Title = "List task boards", ReadOnly = true, UseStructuredContent = true)]
	[Description("List task boards in a project, each with its kind, specBoard (work->spec link, if set) and closed flag. Requires tasks:read.")]
	public static async Task<BoardListResult> BoardListAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await tasks.ListBoardsAsync(projectKey, ct);
		return new BoardListResult(list.Select(b => new BoardRow(b.Name, b.Kind, b.Description, b.SpecBoard, b.CreatedAt, b.ClosedAt != null)).ToList());
	}

	[McpServerTool(Name = "tasks.board_delete", Title = "Delete a task board", Destructive = true, UseStructuredContent = true)]
	[Description("Delete a task board and its nodes. Requires tasks:write.")]
	public static async Task<BoardDeletedResult> BoardDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new BoardDeletedResult(await tasks.DeleteBoardAsync(projectKey, board, ct));
	}

	[McpServerTool(Name = "tasks.board_close", Title = "Close (archive) a task board", UseStructuredContent = true)]
	[Description("Close a board: it rejects further writes (so agents stop writing to it by inertia) but stays readable; history is kept. Reopen with tasks.board_reopen. Requires tasks:write.")]
	public static async Task<BoardClosedResult> BoardCloseAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new BoardClosedResult(await tasks.SetClosedAsync(projectKey, board, true, ct));
	}

	[McpServerTool(Name = "tasks.board_reopen", Title = "Reopen a closed task board", UseStructuredContent = true)]
	[Description("Reopen a closed board so it accepts writes again. Requires tasks:write.")]
	public static async Task<BoardReopenedResult> BoardReopenAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new BoardReopenedResult(await tasks.SetClosedAsync(projectKey, board, false, ct));
	}

	[McpServerTool(Name = "tasks.methodology_enable", Title = "Enable the methodology quartet", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyView))]
	[Description("Provision the four singleton methodology boards (intake/ideas/spec/work) if missing and auto-wire work->spec. Idempotent — opt-in; a project's methodology lives on these, ad-hoc work stays on simple boards. The four kinds are one-per-project. Requires tasks:write. Returns the quartet surface (intake→ideas→spec→work).")]
	public static async Task<object> MethodologyEnableAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return await tasks.EnableMethodologyAsync(projectKey, ct);
	});

	[McpServerTool(Name = "tasks.methodology_get", Title = "Get the methodology quartet", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyView))]
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
		return await tasks.GetMethodologyAsync(projectKey, bodyLen, includeBoards, urlPrefix, ct);
	});

	[McpServerTool(Name = "tasks.get", Title = "Get a board's nodes", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(PlanBoardView))]
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
		subtree. Pass `groupBy` instead to get the tag PROJECTION: an ORDERED, comma-separated
		list of tag namespaces (e.g. "area" or "area,concern") buckets nodes by their value in
		each namespace ("(none)" for untagged), nested in that order, each group with a delivery
		roll-up — the cross-cutting view a single-parent tree can't give. The projection is a
		view; part_of is untouched. Bodies are returned in FULL by default; pass `bodyLen` > 0
		for a per-node snippet (first N chars + "…"), then fetch a full body from the node's
		detail page or a narrower `under`. Requires tasks:read.
		""")]
	public static async Task<object> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, bool includeClosed = false, string? under = null,
		[Description("Tag PROJECTION: an ordered, comma-separated list of tag namespaces (e.g. \"area\" or \"area,concern\"); order sets nesting.")] string? groupBy = null,
		[Description("Snippet length (chars) per node body; 0 (default) = full body. \"…\" appended when cut. Ignored with groupBy (keys only).")] int bodyLen = 0,
		[Description("Include an absolute `url` permalink to each node's detail page (off by default; ignored with groupBy).")] bool includeUrl = false,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		if (!string.IsNullOrWhiteSpace(groupBy))
			return await tasks.GetGroupedAsync(projectKey, board, ParseGroupBy(groupBy), ct);
		var view = await tasks.GetAsync(projectKey, board, includeClosed, under, await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct), ct);
		// Snippet slicing is MCP-adapter-only: the service GetAsync still returns full bodies,
		// which the Razor board renders — so this never starves the UI (spec read-snippet-on-demand).
		return bodyLen <= 0
			? (object)view
			: view with { Nodes = view.Nodes.Select(n => n with { Body = ModuleMcp.SnippetBody(n.Body, bodyLen) ?? n.Body }).ToList() };
	});

	[McpServerTool(Name = "tasks.search", Title = "Search plan nodes", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(TaskSearchResultView))]
	[Description("""
		Hybrid search over the project's active, non-terminal plan nodes (name/body/tags):
		lexical FTS5 (token/prefix, so paraphrases hit) fused with semantic vector similarity
		(RRF), ranked by relevance. `board` scopes to one board (omit = search every board).
		`lexical`/`semantic` (default both on) toggle each retriever; semantic is silently off
		when no embedding capability is configured. Each hit carries key, nodeId, board, status,
		type, title, priority, tags, version (the node's tasks.upsert baseline), links
		(`spec`/`blockedBy`/`linkedTasks`/`supersedes`) and
		(spec boards) the computed `delivery` — bodies are full unless `bodyLen` > 0 (snippet).
		Bounded by `limit` (default 20; 0 = no limit). Response includes `retrievers`
		{ lexical, semantic, degraded }. Requires tasks:read.
		""")]
	public static async Task<object> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string query, string? board = null,
		[Description("Snippet length (chars) per node body; 0 (default) = full body. \"…\" appended when cut.")] int bodyLen = 0,
		[Description("Max nodes returned (default 20; 0 = no limit).")] int limit = 20,
		[Description("Run the lexical FTS retriever (default true).")] bool? lexical = null,
		[Description("Run the semantic vector retriever (default true; no-op when embedding is unavailable).")] bool? semantic = null,
		[Description("Include an absolute `url` permalink to each node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		var res = await tasks.SearchAsync(projectKey, query, board, lexical, semantic, urlPrefix, ct);
		var capped = limit > 0 ? res.Hits.Take(limit) : res.Hits;
		var nodes = capped.Select(h => SearchHit(h, bodyLen)).ToList();
		return new TaskSearchResultView(
			nodes,
			new RetrieverInfo(res.Retrievers.Lexical, res.Retrievers.Semantic, res.Retrievers.Degraded));
	});

	// Wire shape for one search hit: a compact, board-aware projection of the enriched node
	// view (board carried since search spans boards), body sliced to bodyLen (full when 0).
	static TaskSearchNodeView SearchHit(TaskSearchHit h, int bodyLen)
	{
		var n = h.Node;
		return new TaskSearchNodeView(
			Key: n.Key,
			NodeId: n.NodeId,
			Board: h.Board,
			ParentSlug: n.ParentSlug,
			Depth: n.Depth,
			Status: n.Status,
			Type: n.Type,
			Title: n.Title,
			Body: ModuleMcp.SnippetBody(n.Body, bodyLen),
			Priority: n.Priority,
			Delivery: n.Delivery,
			Spec: n.Spec,
			BlockedBy: n.BlockedBy,
			LinkedTasks: n.LinkedTasks,
			Supersedes: n.Supersedes,
			Tags: n.Tags,
			Version: n.Version,
			Url: n.Url);
	}

	// Split a comma-separated groupBy ("area,concern") into the ordered dimension list the
	// service expects; blanks dropped, order and dups preserved (service validates namespaces).
	static string[] ParseGroupBy(string groupBy) =>
		groupBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	[McpServerTool(Name = "tasks.upsert", Title = "Upsert plan nodes", UseStructuredContent = true, OutputSchemaType = typeof(UpsertResultView))]
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

		To DELETE a node, pass { key, deleted:true } (optional version baseline; 0 = delete
		regardless) — the node is soft-closed (history kept), its edges and tags are closed, and
		its key appears in `removed[]`. A node with active part_of children is refused (Rejected
		conflict) — delete the children first, or the whole subtree in one call. deleted cannot
		combine with prevKey. Spec-node deletes need no ideaRef (erasing junk is not a spec
		change — retiring a real requirement stays `deprecated`).

		Returns { applied, currentVersion, inserted, closed, conflicts[], added[], updated[],
		removed[] }; added/updated carry the node (key, nodeId, status, type, title,
		commitRef, priority, version) but NOT `body` by default — the echo is a compact
		cursor-advance, not a re-dump (pass bodyLen > 0 for a sliced body, "…" when cut).
		The delta IS the fresh state since `sinceVersion` — advance your cursor and merge.
		CURSOR CONTRACT: pass the PREVIOUS response's `currentVersion` as the next
		`sinceVersion` (NOT a single node's `version`, which is smaller — that re-echoes the
		whole recent delta; the default 0 echoes every node, bodiless).
		""")]
	public static async Task<object> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board,
		[Description("Array of node objects: flat `key`, optional `partOf` (parent slug|NodeId), `tags` (array of ns:value), `specRef`, `ideaRef`, `blockedBy`, `supersedes`, status/type/title/body/commitRef/priority/version, and `prevKey` to rename.")] PlanNodeInput[] nodes,
		[Description("Cursor: pass the prior response's `currentVersion` so the echo is just your delta. 0 (default) echoes every node (bodiless).")] long sinceVersion = 0,
		[Description("Slice length (chars) of each echoed node body; 0 (default) = no body (compact echo). \"…\" appended when cut.")] int bodyLen = 0,
		[Description("Include an absolute `url` permalink to each returned node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var patches = ParseNodePatches(nodes);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.UpsertAsync(projectKey, board, patches, sinceVersion, ct), urlPrefix, bodyLen);
	});

	[McpServerTool(Name = "tasks.delta", Title = "Plan delta since cursor", ReadOnly = true, UseStructuredContent = true)]
	[Description("Return nodes added/updated/removed since `sinceVersion` (no writes); bodies omitted unless bodyLen > 0 (compact by default). Requires tasks:read.")]
	public static async Task<UpsertResultView> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, long sinceVersion,
		[Description("Slice length (chars) of each node body; 0 (default) = no body (compact). \"…\" appended when cut.")] int bodyLen = 0,
		[Description("Include an absolute `url` permalink to each returned node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.DeltaAsync(projectKey, board, sinceVersion, ct), urlPrefix, bodyLen);
	}

	[McpServerTool(Name = "tasks.workflow", Title = "Board workflow (kinds/statuses/transitions)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(WorkflowView))]
	[Description("Return the workflow for a board: its kind and the task types it hosts, each with statuses (slug, name, kind=open|terminalok|terminalcancel), the initial status, and transitions (from, to, requiresApproval, requiresReason). Use this to learn the legal statuses before tasks.upsert. Requires tasks:read.")]
	public static async Task<object> WorkflowAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var kind = await tasks.ResolveKindAsync(projectKey, board, ct);
		return new WorkflowView(
			Kind: kind.ToString().ToLowerInvariant(),
			Types: WorkflowCatalog.Types(kind).Select(w => new WorkflowTypeView(
				Type: w.Type,
				Initial: w.Initial,
				Statuses: w.Statuses.Select(s => new WorkflowStatusView(s.Slug, s.Name, s.Kind.ToString().ToLowerInvariant())).ToList(),
				Transitions: w.Transitions.Select(t => new WorkflowTransitionView(t.From, t.To, t.RequiresApproval, t.RequiresReason)).ToList())).ToList());
	});

	// ---- adapter plumbing: JSON parsing + wire shaping (no domain logic) ----

	// Build the absolute permalink prefix for this project's nodes
	// ("{scheme}://{host}/ui/{ws}/{project}/tasks/node/"), or null when include_url is off or
	// the workspace can't be resolved. Per-node url = prefix + "{board}/{slug}" (the canonical
	// slug-URL, node-slug-addressable); the prefix ends with "/tasks/". scheme/host come from
	// the request (honor forwarded headers behind a proxy).
	static async Task<string?> UrlPrefixAsync(IHttpContextAccessor http, ITasksService tasks, string projectKey, bool includeUrl, CancellationToken ct)
	{
		if (!includeUrl) return null;
		var req = http.HttpContext?.Request;
		if (req is null) return null;
		var ws = await tasks.ResolveWorkspaceAsync(projectKey, ct);
		if (string.IsNullOrEmpty(ws)) return null;
		return $"{req.Scheme}://{req.Host}{Routes.ProjectTasks(ws, projectKey)}/";
	}

	static UpsertResultView Serialize(UpsertOutcome o, string? urlPrefix = null, int bodyLen = 0)
	{
		var r = o.Result;
		return new UpsertResultView(
			Applied: r.Applied,
			CurrentVersion: r.CurrentVersion,
			Kind: o.Kind.ToString().ToLowerInvariant(),
			Inserted: r.Inserted,
			Closed: r.Closed,
			Conflicts: r.Conflicts.Select(c => new UpsertConflictView(c.Key, c.Kind.ToString(), c.BaselineVersion, c.ActiveVersion, c.Reason)).ToList(),
			Added: r.Added.Select(n => NodeDto(n, urlPrefix, bodyLen)).ToList(),
			Updated: r.Updated.Select(n => NodeDto(n, urlPrefix, bodyLen)).ToList(),
			Removed: r.Removed.ToList());
	}

	// Delta projection of a node (no links/delivery/tags — that's tasks.get). camelCased by the
	// serializer; `body` is sliced to bodyLen (null when 0 → omitted) for the compact echo.
	static PlanNodeDelta NodeDto(PlanNode n, string? urlPrefix = null, int bodyLen = 0) => new(
		Key: n.Key,
		NodeId: n.NodeId,
		Status: n.Status,
		Type: n.Type,
		Title: n.Name,
		Body: ModuleMcp.SliceBody(n.Body, bodyLen),
		CommitRef: n.CommitRef,
		Priority: n.Priority,
		Version: n.Version,
		Url: urlPrefix is null ? null : urlPrefix + n.Board + "/" + n.Key);

	// Map the typed node inputs into service NodePatches. Read-merge (inheriting omitted fields
	// from the prior row) happens in the service; here an omitted field deserializes to null
	// (inherit) and a present field to its value ("" = explicit clear) — the null-vs-"" distinction
	// is carried by the JSON value itself, so the old Has()-presence checks are no longer needed.
	static List<NodePatch> ParseNodePatches(PlanNodeInput[] nodes)
	{
		var list = new List<NodePatch>(nodes.Length);
		foreach (var n in nodes)
		{
			if (n.Deleted && ResolvePrevKey(n) is not null)
				throw new ArgumentException("a node cannot be renamed and deleted in the same patch");
			list.Add(new NodePatch
			{
				Key = ResolveKey(n),
				PrevKey = ResolvePrevKey(n),
				Deleted = n.Deleted,
				Version = n.Version,
				Status = n.Status,
				Type = n.Type,
				Title = n.Title,
				Body = n.Body,
				// CommitRef: null = omit (don't touch), any non-null (incl. "") = explicit set/clear.
				// Carries the old CommitRefSet presence bit via null-ness of the typed field.
				CommitRefSet = n.CommitRef is not null,
				CommitRef = n.CommitRef,
				Priority = n.Priority,
				SpecRef = n.SpecRef,
				IdeaRef = n.IdeaRef,
				BlockedBy = n.BlockedBy,
				PartOf = n.PartOf,
				Supersedes = n.Supersedes,
				// Enforced tags: null = omit (inherit); a non-null list (incl. empty) REPLACES the set.
				Tags = n.Tags,
			});
		}
		return list;
	}

	// A node's address is a flat board-unique slug in `key` (`l1` accepted as an alias).
	// Nesting is the `partOf` parent, not the key. Validated/normalized via TaskSlug.
	static string ResolveKey(PlanNodeInput n)
	{
		var key = !string.IsNullOrEmpty(n.Key) ? n.Key : n.L1;
		if (!string.IsNullOrEmpty(key))
			return TaskSlug.Validate(key);
		throw new ArgumentException("each node needs a 'key' (a flat slug)");
	}

	static string? ResolvePrevKey(PlanNodeInput n)
	{
		var prevKey = !string.IsNullOrEmpty(n.PrevKey) ? n.PrevKey : n.PrevL1;
		return !string.IsNullOrEmpty(prevKey) ? TaskSlug.Validate(prevKey) : null;
	}
}
