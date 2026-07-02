using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
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
	[McpServerTool(Name = "tasks.board_create", Title = "Create a task board", UseStructuredContent = true, OutputSchemaType = typeof(BoardCreatedResult))]
	[Description("CREATE a named task board in a project. `kind` sets the board role (simple|spec|ideas|intake|work; default simple) which drives the workflow — call tasks.workflow to see the valid types/statuses/transitions for a kind. `specBoard` (work boards only) names the spec board this board's tasks link into, so specRef targets are validated against it and the agent need not guess. Requires tasks:write.")]
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

	[McpServerTool(Name = "tasks.board_set_spec", Title = "Set a work board's spec board", UseStructuredContent = true, OutputSchemaType = typeof(BoardSetSpecResult))]
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

	[McpServerTool(Name = "tasks.board_list", Title = "List task boards", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(BoardListResult))]
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

	[McpServerTool(Name = "tasks.board_delete", Title = "Delete a task board", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(BoardDeletedResult))]
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

	[McpServerTool(Name = "tasks.board_close", Title = "Close (archive) a task board", UseStructuredContent = true, OutputSchemaType = typeof(BoardClosedResult))]
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

	[McpServerTool(Name = "tasks.board_reopen", Title = "Reopen a closed task board", UseStructuredContent = true, OutputSchemaType = typeof(BoardReopenedResult))]
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
	public static async Task<MethodologyView> MethodologyEnableAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return await tasks.EnableMethodologyAsync(projectKey, ct);
	}

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
		quartet boards (kinds: intake|ideas|spec|work). The index has a HARD OUTPUT BUDGET:
		`counts` per board is always complete, but node rows share a response-wide char budget
		spent in pipeline order — when a board's rows no longer fit it is cut and flagged with
		`truncated:true` + `omitted` (rows dropped), and the response carries a top-level
		`hint` on how to narrow (includeBoards one board at a time, bodyLen:0, or tasks.search
		with board + `under` for subtree detail). No markers = the complete index. For full
		untruncated bodies or subtree drill-down, use tasks.search (the listing/detail
		read verb). `enabled` is true when all four singleton boards exist. Requires tasks:read.
		""")]
	public static async Task<MethodologyView> MethodologyGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Slice length (chars) of each node body to include; 0 = index only (no bodies). \"…\" is appended when a body is cut.")] int bodyLen = 0,
		[Description("Restrict to these quartet boards by kind (intake|ideas|spec|work); empty/omitted = all four.")] string[]? includeBoards = null,
		[Description("Include an absolute `url` permalink to each node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return await tasks.GetMethodologyAsync(projectKey, bodyLen, includeBoards, urlPrefix, ct);
	}

	[McpServerTool(Name = "tasks.methodology_def_upsert", Title = "Define the project's methodology (user-defined kinds/FSMs)", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyDefUpsertResult))]
	[Description("""
		Store the project's USER-DEFINED METHODOLOGY DEFINITION — the document that describes
		the project's own board kinds, task types, statuses and transitions as data (NOT the
		quartet index: tasks.methodology_get reads the intake/ideas/spec/work BOARDS; this
		verb writes the process DEFINITION). One definition per project, versioned: `version`
		is the baseline you last saw (0 = the project has none yet); a moved baseline is a
		clear conflict error naming the current version — re-read with
		tasks.methodology_def_get and resubmit. The definition is validated as a whole before
		it is stored (name/kind/type slugs, ≥1 kind, ≥1 workflow block per kind, type unique
		within its kind, statuses non-empty and unique per block, every transition between
		statuses of ITS block, no duplicate edges). `definition` shape: { name, kinds:[{
		kind, quickAddAllowed?, workflows:[{ types:[...], statuses:[{ slug, name?,
		kind?: open|terminalok|terminalcancel }], transitions:[{ from, to,
		requiresApproval?, requiresReason?, preconditionArtifact? }] }] }] }; statuses[0] is
		the initial status; `preconditionArtifact` names a comment-artifact tag (e.g.
		"spec_plan") the node must carry before the transition. NOTE (wave 1.1): storage +
		validation only — live boards still run the built-in preset until the engine ships;
		enforcement of preconditionArtifact also lands with the engine. Returns { version
		(the new baseline), changed (false = identical resubmit, no new revision) }.
		Requires tasks:write.
		""")]
	public static async Task<MethodologyDefUpsertResult> MethodologyDefUpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("The whole methodology definition (structured document; see the tool description for the shape).")] MethodologyDefInput definition,
		[Description("Baseline version you last saw; 0 = the project has no definition yet.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var ack = await tasks.DefineMethodologyAsync(projectKey, ParseDefinition(definition), version, ct);
		return new MethodologyDefUpsertResult(ack.Version, ack.Changed);
	}

	[McpServerTool(Name = "tasks.methodology_def_get", Title = "Get the project's methodology definition", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyDefGetResult))]
	[Description("""
		Return the project's USER-DEFINED METHODOLOGY DEFINITION — the stored process
		document (kinds/types/statuses/transitions as data), NOT the quartet board index
		(that is tasks.methodology_get). Defined=true → { name, kinds:[{ kind,
		quickAddAllowed, workflows:[{ types, initial, statuses:[{ slug, name, kind }],
		transitions:[{ from, to, requiresApproval, requiresReason, preconditionArtifact? }]
		}] }], version (the baseline for tasks.methodology_def_upsert), created, updated }.
		Defined=false → the project has no definition of its own and runs on the built-in
		preset (`preset` names it) — an honest state, not an error. Requires tasks:read.
		""")]
	public static async Task<MethodologyDefGetResult> MethodologyDefGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var view = await tasks.GetMethodologyDefinitionAsync(projectKey, ct);
		if (view is null)
			return new MethodologyDefGetResult(Defined: false, Preset: BuiltinPreset);
		return new MethodologyDefGetResult(
			Defined: true,
			Name: view.Definition.Name,
			Kinds: view.Definition.Kinds.Select(k => new MethodologyKindView(
				k.Kind, k.QuickAddAllowed,
				k.Workflows.Select(w => new MethodologyWorkflowBlockView(
					Types: w.Types,
					Initial: w.Initial,
					Statuses: w.Statuses.Select(s => new WorkflowStatusView(s.Slug, s.Name, s.Kind.ToString().ToLowerInvariant())).ToList(),
					Transitions: w.Transitions.Select(t => new MethodologyTransitionView(t.From, t.To, t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact)).ToList())).ToList())).ToList(),
			Version: view.Version,
			Created: view.Created,
			Updated: view.Updated);
	}

	// The `preset` a definition-less project runs on: the hardcoded WorkflowCatalog
	// (simple + the methodology quartet kinds).
	const string BuiltinPreset = "builtin-workflow-catalog";

	// Map the typed wire document onto the domain definition 1:1 (nulls → empty lists —
	// the validator then reports "needs at least one ..." instead of an opaque NRE).
	// Only the status-kind STRING needs parsing here; integrity stays in the service.
	static MethodologyDefinition ParseDefinition(MethodologyDefInput d) => new(
		d.Name ?? string.Empty,
		(d.Kinds ?? []).Select(k => new MethodologyKindDef(
			k.Kind ?? string.Empty,
			k.QuickAddAllowed,
			(k.Workflows ?? []).Select(w => new MethodologyWorkflowDef(
				w.Types ?? [],
				(w.Statuses ?? []).Select(ParseStatus).ToList(),
				(w.Transitions ?? []).Select(t => new MethodologyTransitionDef(
					t.From ?? string.Empty, t.To ?? string.Empty,
					t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact)).ToList())).ToList())).ToList());

	static WorkflowStatus ParseStatus(MethodologyStatusInput s)
	{
		var slug = s.Slug ?? string.Empty;
		var kind = StatusKind.Open;
		if (!string.IsNullOrWhiteSpace(s.Kind) && !Enum.TryParse(s.Kind.Trim(), ignoreCase: true, out kind))
			throw new ArgumentException($"status '{slug}': kind '{s.Kind}' is not a status kind (valid: open|terminalok|terminalcancel)");
		return new WorkflowStatus(slug, string.IsNullOrWhiteSpace(s.Name) ? slug : s.Name, kind);
	}

	[McpServerTool(Name = "tasks.node_get", Title = "Get one node in full", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(NodeDetailView))]
	[Description("""
		Return ONE node of a board in FULL, addressed by `node` = its slug key OR its 32-hex
		NodeId (the same slug-or-NodeId convention as specRef/partOf). The answer carries the
		owning `board`, its `kind`, the part_of `ancestors` chain (root→parent), and the
		fully-enriched node: key, nodeId, parentNodeId/parentSlug/depth, status, type, title,
		the COMPLETE `body` (never truncated), priority, version, tags, links (`spec`,
		`blockedBy`; on a spec node `linkedTasks` + the computed `delivery`), plus `url` when
		includeUrl. An addressed read ignores terminality: a Done/Cancelled/deprecated node is
		returned like any other (no includeClosed needed). A node that doesn't exist on the
		board is a clear error, not an empty result. Use this instead of re-fetching a whole
		board when you need one node's full body. Requires tasks:read.
		""")]
	public static async Task<NodeDetailView> NodeGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board,
		[Description("The node's slug key on the board, or its 32-hex NodeId.")] string node,
		[Description("Include an absolute `url` permalink to the node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return await tasks.GetNodeOnBoardAsync(projectKey, board, node, urlPrefix, ct);
	}

	[McpServerTool(Name = "tasks.search", Title = "Read plan nodes (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(TaskSearchResultView))]
	[Description("""
		THE read verb for plan nodes — one tool for both LISTING and SEARCH (list = search
		without `q`; replaces the former tasks.get). Nodes are FLAT (a single slug `key`);
		hierarchy is the part_of edge, surfaced as parentNodeId/parentSlug and a computed
		`depth` (0 = root) — build the tree from those. Every row carries its `board` plus
		key, nodeId, status, type, title, body, priority, version, renamedFrom, `tags`, and
		links: `spec` (spec nodes a task implements), `blockedBy`, and on a spec board
		`linkedTasks` + the COMPUTED `delivery` roll-up (not_started|in_progress|done|
		done_with_defects).

		MODES. Without `q`: a DETERMINISTIC listing — `board` scopes to one board (the
		response then carries the board context: `kind`, `specBoard`, `currentVersion`);
		omit `board` for a project-wide list. Default order: priority then key. Terminal/
		closed nodes are HIDDEN unless includeClosed=true (closed part_of ancestors of a
		visible node are kept so the tree stays connected). With `q`: a RELEVANCE selection
		via hybrid search over name/body/tags (lexical FTS5 ⊕ semantic vectors, RRF-fused;
		semantic is silently absent when no embedding is configured) over the OPEN
		(non-terminal) set; the fused ranking supplies a bounded candidate pool of
		max(3×limit, 50). Default order: relevance; the response carries `retrievers`
		{lexical, semantic, degraded}.

		FILTERS (predicates in BOTH modes): `under` = a part_of subtree root (slug or
		NodeId; a slug resolves on `board`, or project-wide when board is omitted);
		`status` = keep only these slugs (case-insensitive; naming a TERMINAL status
		returns its nodes even without includeClosed — an explicit ask; an unknown slug is
		rejected); `keys` = address specific nodes (slug|NodeId mixed, resolved like
		tasks.node_get — a miss or an ambiguous cross-board slug is a clear error, and an
		addressed terminal node is returned without includeClosed).

		SORT: `sort` = {by: priority|created|updated|title|relevance, desc?}. Without `q`
		the default is priority (asking for relevance is an error); with `q` the default is
		relevance, and an explicit sort reorders WITHIN the relevance-selected set (`desc`
		is ignored for relevance). `limit` caps the rows (with `q` it defaults to 20, 0 =
		no cap; a listing is unbounded by default — the output budget still applies).

		PROJECTION: `groupBy` = an ORDERED, comma-separated list of tag namespaces (e.g.
		"area" or "area,concern") returns the tag-bucket view instead of rows (`groups`
		nested in that order, "(none)" for untagged, each with a delivery roll-up); needs
		`board` and does NOT combine with `q` (a projection is a view, not a ranking).

		Bodies are FULL by default; `bodyLen` > 0 snippets each body (first N chars + "…")
		— fetch one full body via tasks.node_get. The response has a HARD OUTPUT BUDGET
		(~30k serialized chars): overflowing rows are prefix-cut in result order and
		flagged `truncated:true` + `omitted` + a narrowing `hint`; no markers = the
		complete answer.

		Examples: {board:"work"} → the work board; {board:"work", status:["Review"]} →
		what awaits review; {q:"vector index cursor"} → related nodes anywhere;
		{q:"flaky tests", board:"work", sort:{by:"updated", desc:true}, bodyLen:200} →
		recent matches, snippeted; {keys:["node-comments-v1"]} → one addressed row (any
		status). Requires tasks:read.
		""")]
	public static async Task<TaskSearchResultView> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Search query. Omit for a deterministic listing (list = search without q).")] string? q = null,
		[Description("Scope to one board (listing then carries kind/specBoard/currentVersion). Omit = the whole project; each row names its board.")] string? board = null,
		[Description("Restrict to the part_of subtree under this node (slug or 32-hex NodeId).")] string? under = null,
		[Description("Keep only these status slugs (case-insensitive). A terminal status listed here is returned even when includeClosed=false.")] string[]? status = null,
		[Description("Address specific nodes: slugs and/or 32-hex NodeIds, mixed (resolved like tasks.node_get; terminal nodes included).")] string[]? keys = null,
		[Description("Include terminal/closed nodes in a listing (search covers the open set only).")] bool includeClosed = false,
		[Description("Sort order: {by: priority|created|updated|title|relevance, desc?}. Default: priority (listing) / relevance (with q).")] SortInput? sort = null,
		[Description("Tag PROJECTION instead of rows: an ordered, comma-separated list of tag namespaces (e.g. \"area,concern\"). Needs board; not with q.")] string? groupBy = null,
		[Description("Snippet length (chars) per node body; 0 (default) = full body. \"…\" appended when cut.")] int bodyLen = 0,
		[Description("Max rows returned. Default: unbounded listing / 20 with q (0 = no cap).")] int? limit = null,
		[Description("Include an absolute `url` permalink to each node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);

		var hasQuery = !string.IsNullOrWhiteSpace(q);
		if (!string.IsNullOrWhiteSpace(groupBy))
		{
			// The tag projection is a deterministic single-board VIEW — routing it against a
			// relevance selection would silently change what the buckets mean, so q is refused.
			if (hasQuery)
				throw new ArgumentException("groupBy and q don't combine — the tag projection is a deterministic view, a query is a relevance selection; drop one of them");
			if (string.IsNullOrWhiteSpace(board))
				throw new ArgumentException("groupBy needs a board — the tag projection is a single-board view");
			var g = await tasks.GetGroupedAsync(projectKey, board, ParseGroupBy(groupBy), ct);
			return new TaskSearchResultView([], Board: board, Kind: g.Kind, GroupBy: g.GroupBy, Groups: g.Groups);
		}

		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		var res = await tasks.SearchNodesAsync(projectKey, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Query = hasQuery ? q : null,
			Filter = new TaskNodeFilter(board, under, status, keys, includeClosed),
			Sort = ParseSort(sort),
			Limit = limit ?? (hasQuery ? DefaultSearchLimit : 0),
			BodyLen = bodyLen,
		}, urlPrefix, ct);

		// Response budget (MCP-adapter-only): measured on the wire form of the rows as they
		// will be sent (bodies already sliced by the service), prefix-cut, marked — never silent.
		var rows = res.Hits.Select(SearchRow).ToList();
		var (kept, omitted) = new ResponseBudget().Take(rows);
		return new TaskSearchResultView(
			kept, res.Board, res.Kind, res.SpecBoard, res.CurrentVersion,
			Retrievers: res.Retrievers is { } r ? new RetrieverInfo(r.Lexical, r.Semantic, r.Degraded) : null,
			Truncated: omitted > 0 ? true : null,
			Omitted: omitted > 0 ? omitted : null,
			Hint: omitted > 0 ? SearchBudgetHint : null);
	}

	// With a query the result is capped even when the caller asks for nothing specific —
	// the candidate pool (max(3×limit, 50)) and this default keep the answer bounded.
	const int DefaultSearchLimit = 20;

	// Surfaced on TaskSearchResultView.Hint when the rows were cut by the response budget.
	const string SearchBudgetHint =
		"Output budget exceeded: node rows were truncated (see truncated/omitted). Narrow the " +
		"read: `board` (one board), `under` (one part_of subtree), `status` (only the statuses " +
		"you need), `keys` (address specific nodes), `bodyLen` (snippet bodies), a smaller " +
		"`limit`, `groupBy` (keys-only tag projection), or tasks.node_get for one full node.";

	// Map the wire `sort` argument onto the service sort axis; an unknown axis is a clear error.
	static (TaskSortBy By, bool Desc)? ParseSort(SortInput? sort)
	{
		if (sort is null || string.IsNullOrWhiteSpace(sort.By)) return null;
		if (!Enum.TryParse<TaskSortBy>(sort.By.Trim(), ignoreCase: true, out var by))
			throw new ArgumentException($"sort.by '{sort.By}' is not a sort axis (valid: priority|created|updated|title|relevance)");
		return (by, sort.Desc);
	}

	// Wire shape for one row: the enriched node view flattened with its owning board (rows
	// may span boards). RenamedFrom is omitted when empty (null → dropped by the serializer).
	static TaskSearchNodeView SearchRow(TaskSearchHit h)
	{
		var n = h.Node;
		return new TaskSearchNodeView(
			Key: n.Key,
			NodeId: n.NodeId,
			Board: h.Board,
			ParentNodeId: n.ParentNodeId,
			ParentSlug: n.ParentSlug,
			Depth: n.Depth,
			Status: n.Status,
			Type: n.Type,
			Title: n.Title,
			Body: n.Body,
			CommitRef: n.CommitRef,
			Priority: n.Priority,
			Delivery: n.Delivery,
			Spec: n.Spec,
			BlockedBy: n.BlockedBy,
			LinkedTasks: n.LinkedTasks,
			Supersedes: n.Supersedes,
			RenamedFrom: n.RenamedFrom is { Count: > 0 } rf ? rf : null,
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
		Declarative PATCH per node (omitted field = unchanged; tags: [] clears, omit leaves
		as-is) — a temporal upsert of plan nodes. Requires tasks:write.

		Each node has a FLAT `key` — a single slug [a-z][a-z0-9_-]{0,99} (no '/'; the old
		l1/l2/l3 path is gone). Nesting is the `partOf` field: a parent slug (on this board)
		or a NodeId — null omits it, "" detaches to a root. A node may carry multiple parents'
		worth of grouping via `tags` (an array of "namespace:value", namespaces area|concern;
		[] clears, omit leaves as-is). Give each node a `title` and `body` (markdown). Other
		fields: status (slug — see tasks.workflow), type (feature|bug|chore on work boards;
		chore = spec-less engineering hygiene), specRef (the spec node the work task
		implements, as its slug on the linked spec board or a NodeId — REQUIRED for a new
		feature/bug), ideaRef (ON A SPEC BOARD: the NodeId of the
		`accepted` idea this create/change is made under — REQUIRED for every spec node; becomes
		the idea_spec edge), blockedBy (the blocking node as its slug on THIS board or a
		NodeId — the same slug-or-NodeId convention as specRef/partOf), supersedes
		(a slug|NodeId this node replaces — the old one is moved to its terminal-cancel),
		commitRef?, priority? (sparse int, lower first), version (baseline you last saw; 0 =
		new). Rename via prevKey. A cold call auto-creates the board.

		To DELETE a node, pass { key, deleted:true } (optional version baseline; 0 = delete
		regardless) — the node is soft-closed (history kept), its edges and tags are closed, and
		its key appears in `removed[]`. A node with active part_of children is refused (Rejected
		conflict) — delete the children first, or the whole subtree in one call. deleted cannot
		combine with prevKey. Spec-node deletes need no ideaRef (erasing junk is not a spec
		change — retiring a real requirement stays `deprecated`).

		Returns the pure write-ack { applied, currentVersion, inserted, closed, conflicts[],
		added[], updated[], removed[] }. The echo covers ONLY this call: added/updated/removed
		carry the call's own nodes plus nodes its cascade effects touched (a `supersedes`
		target obsoleted, a deleted subtree, an unblocked task) — never other writers'
		history, and there is no cursor parameter on a write. added/updated carry the node
		(key, nodeId, status, type, title, commitRef, priority, version) but NOT `body` by
		default — the echo is a compact ack, not a re-dump (pass bodyLen > 0 for a sliced
		body, "…" when cut). `currentVersion` is the board-wide cursor: for a full delta
		since a cursor (everything changed by anyone — rebase/merge/catch-up), call
		tasks.delta with it as `sinceVersion`.
		""")]
	public static async Task<UpsertResultView> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board,
		[Description("Array of node objects: flat `key`, optional `partOf` (parent slug|NodeId), `tags` (array of ns:value), `specRef` (spec slug|NodeId), `ideaRef`, `blockedBy` (blocker slug|NodeId), `supersedes`, status/type/title/body/commitRef/priority/version, and `prevKey` to rename.")] PlanNodeInput[] nodes,
		[Description("Slice length (chars) of each echoed node body; 0 (default) = no body (compact echo). \"…\" appended when cut.")] int bodyLen = 0,
		[Description("Include an absolute `url` permalink to each returned node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var patches = ParseNodePatches(nodes);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.UpsertAsync(projectKey, board, patches, ct), urlPrefix, bodyLen);
	}

	[McpServerTool(Name = "tasks.delta", Title = "Plan delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(UpsertResultView))]
	[Description("Return nodes added/updated/removed since `sinceVersion` (no writes) — THE cursor/catch-up surface (a tasks.upsert ack echoes only its own call; pass its `currentVersion` here for the full board delta). Bodies omitted unless bodyLen > 0 (compact by default). Requires tasks:read.")]
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
	[Description("Return the workflow for a board: its kind plus `workflows` — one block per DISTINCT state machine, each carrying `types` (every type slug sharing that FSM; e.g. feature|bug|chore on a work board are one block), the initial status, statuses (slug, name, kind=open|terminalok|terminalcancel) and transitions (from, to, requiresApproval, requiresReason). Use this to learn the legal types/statuses before tasks.upsert. Requires tasks:read.")]
	public static async Task<WorkflowView> WorkflowAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var kind = await tasks.ResolveKindAsync(projectKey, board, ct);
		return new WorkflowView(
			Kind: kind.ToString().ToLowerInvariant(),
			Workflows: GroupByFsm(kind).Select(g => new WorkflowGroupView(
				Types: g.Types,
				Initial: g.Wf.Initial,
				Statuses: g.Wf.Statuses.Select(s => new WorkflowStatusView(s.Slug, s.Name, s.Kind.ToString().ToLowerInvariant())).ToList(),
				Transitions: g.Wf.Transitions.Select(t => new WorkflowTransitionView(t.From, t.To, t.RequiresApproval, t.RequiresReason)).ToList())).ToList());
	}

	// Collapse a kind's workflows into blocks of types sharing one identical FSM (statuses +
	// transitions, record equality — Initial is Statuses[0], so it's covered). The work board's
	// feature/bug/chore trio collapses to one block; a Simple board reports its whole type
	// vocabulary (type is a label there, not a workflow branch — the catalog's single entry
	// carries the placeholder type "simple", not what tasks.upsert accepts).
	static List<(List<string> Types, Workflow Wf)> GroupByFsm(BoardKind kind)
	{
		var groups = new List<(List<string> Types, Workflow Wf)>();
		foreach (var w in WorkflowCatalog.Types(kind))
		{
			var types = kind == BoardKind.Simple ? WorkflowCatalog.SimpleTypes.ToList() : [w.Type];
			var match = groups.FindIndex(g => SameFsm(g.Wf, w));
			if (match < 0) groups.Add((types, w));
			else groups[match].Types.AddRange(types.Where(t => !groups[match].Types.Contains(t, StringComparer.OrdinalIgnoreCase)));
		}
		return groups;
	}

	static bool SameFsm(Workflow a, Workflow b) =>
		a.Statuses.SequenceEqual(b.Statuses) && a.Transitions.SequenceEqual(b.Transitions);

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

	// Delta projection of a node (no links/delivery/tags — that's tasks.search). camelCased by the
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
