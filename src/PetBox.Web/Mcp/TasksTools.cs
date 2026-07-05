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
	[McpServerTool(Name = "tasks_board_create", Title = "Create a task board", UseStructuredContent = true, OutputSchemaType = typeof(BoardCreatedResult))]
	[Description("CREATE a named task board in a project. `kind` sets the board role (simple|classic|spec|ideas|intake|work, default simple — plus any kind the project's methodology definition declares via tasks_methodology_def_upsert) which drives the workflow — call tasks_workflow to see the valid types/statuses/transitions for a kind; an unknown kind is rejected naming the valid ones. `specBoard` (work boards only) names the spec board this board's tasks link into, so specRef targets are validated against it and the agent need not guess. Requires tasks:write.")]
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

	[McpServerTool(Name = "tasks_board_set_spec", Title = "Set a work board's spec board", UseStructuredContent = true, OutputSchemaType = typeof(BoardSetSpecResult))]
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

	[McpServerTool(Name = "tasks_board_list", Title = "List task boards", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(BoardListResult))]
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

	[McpServerTool(Name = "tasks_board_delete", Title = "Delete a task board", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(BoardDeletedResult))]
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

	[McpServerTool(Name = "tasks_board_close", Title = "Close (archive) a task board", UseStructuredContent = true, OutputSchemaType = typeof(BoardClosedResult))]
	[Description("Close a board: it rejects further writes (so agents stop writing to it by inertia) but stays readable; history is kept. Reopen with tasks_board_reopen. Requires tasks:write.")]
	public static async Task<BoardClosedResult> BoardCloseAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new BoardClosedResult(await tasks.SetClosedAsync(projectKey, board, true, ct));
	}

	[McpServerTool(Name = "tasks_board_reopen", Title = "Reopen a closed task board", UseStructuredContent = true, OutputSchemaType = typeof(BoardReopenedResult))]
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

	[McpServerTool(Name = "tasks_methodology_enable", Title = "Enable a methodology preset", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyEnableResult))]
	[Description("""
		Provision a methodology PRESET's board(s) if missing and (quartet only) auto-wire
		work->spec. `preset` selects which board kind(s) to create (default "quartet" =
		intake/ideas/spec/work — one-per-project singletons; "classic" = one standalone
		classic board (task|feature|bug), NOT a singleton — an unknown slug is rejected
		naming the available presets). Idempotent for the quartet; a rerun on a
		non-singleton preset leaves its existing board(s) alone (use tasks_board_create with
		the preset's kind for another one). Opt-in — a project's methodology lives on these
		boards, ad-hoc work stays on simple boards. Requires tasks:write.

		Returns what THIS CALL's preset provisioned — NOT the methodology quartet index
		(call tasks_methodology_get for that; irrelevant when `preset` isn't "quartet"):
		`preset` (the resolved slug) and `boards[]`, one row per kind the preset declares —
		`kind`, `name` (the board now serving that kind; null only if another board already
		owns that name and nothing could be provisioned), `created` (false on an idempotent
		rerun, or when nothing was created), `counts` (a status histogram, like
		tasks_methodology_get's board rows — no node dump), and `workflow` (the kind's FSM
		blocks, the tasks_workflow shape) so the response is self-contained.
		""")]
	public static async Task<MethodologyEnableResult> MethodologyEnableAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("The methodology preset to provision (default \"quartet\" = intake/ideas/spec/work; \"classic\" = one standalone GitHub/Jira/Linear-level board). Unknown slug → a clear error listing the available presets.")] string? preset = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return await tasks.EnableMethodologyAsync(projectKey, preset ?? MethodologyPresets.DefaultProvisioningPreset, ct);
	}

	[McpServerTool(Name = "tasks_methodology_get", Title = "Get the methodology quartet", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyView))]
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
		`hint` on how to narrow (includeBoards one board at a time, bodyLen:0, or tasks_search
		with board + `under` for subtree detail). No markers = the complete index. For full
		untruncated bodies or subtree drill-down, use tasks_search (the listing/detail
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

	[McpServerTool(Name = "tasks_methodology_def_upsert", Title = "Define the project's methodology (user-defined kinds/FSMs)", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyDefUpsertResult))]
	[Description("""
		Store the project's USER-DEFINED METHODOLOGY DEFINITION — the document that describes
		the project's own board kinds, task types, statuses and transitions as data (NOT the
		quartet index: tasks_methodology_get reads the intake/ideas/spec/work BOARDS; this
		verb writes the process DEFINITION). One definition per project, versioned: `version`
		is the WATERMARK baseline — pass the `version` from your last tasks_methodology_def_get
		(0 = the project has none yet); a stale baseline (someone redefined since) or one ahead of
		the project's cursor is a clear conflict error naming the current version — re-read with
		tasks_methodology_def_get and resubmit. The definition is validated as a whole before
		it is stored (name/kind/type slugs, ≥1 kind, ≥1 workflow block per kind, type unique
		within its kind, statuses non-empty and unique per block, every transition between
		statuses of ITS block, no duplicate edges). `definition` shape: { name, kinds:[{
		kind, quickAddAllowed?, workflows:[{ types:[...], statuses:[{ slug, name?,
		kind?: open|terminalok|terminalcancel }], transitions:[{ from, to,
		requiresApproval?, enforceApproval?, requiresReason?, preconditionArtifact?,
		checklist?:[...] }] }], linkConstraints?:[{ type, link, targetKind?,
		targetStatuses? }], effects?:[{ on, link, direction, set, onlyFrom? }] }],
		linkKinds?:[{ slug, description? }],
		tagAxes?:[{ namespace, description? }] }; statuses[0] is
		the initial status; `preconditionArtifact` names a comment-artifact tag (e.g.
		"spec_plan") the node must carry before the transition (enforced: the upsert refuses
		the transition until an `artifact:<slug>` comment exists on the node).
		`enforceApproval` (only with requiresApproval:true) declares the approval gate as
		server-blocked rather than owner-only by convention; `checklist` is free-text
		conditions to confirm before the transition (guide-rendered, not enforced).
		`linkConstraints` (per kind): "a NEW node of `type` must carry a `link` at creation"
		— link ∈ task_spec|blocks|idea_spec (the kinds expressible in the upsert call as
		specRef/blockedBy/ideaRef); edits don't re-require it; `targetKind`/`targetStatuses`
		optionally declare what the link must point at (a node of that kind / in one of those
		statuses — declaration only, runtime resolution lands with engine v2). `effects`
		(per kind, declaration only — executed once engine v2 ships): on a node of this kind
		ENTERING status `on`, linked nodes over relation `link` in `direction`
		(incoming|outgoing) are set to `set`; `onlyFrom` restricts to linked nodes currently
		in that status. `linkKinds` (project-wide):
		additional relation kinds for relations_create (free semantic edges, no FSM effects;
		must not collide with builtin kinds). `tagAxes` (project-wide): declared tag
		namespaces — when present, tags on definition-resolved boards must be
		`<namespace>:value` from this list (empty/omitted = free-form tags). The definition
		is LIVE: a declared kind can be given to tasks_board_create, its boards resolve
		types/statuses/transitions from this document (tasks_workflow shows them), and any
		other kind keeps the built-in preset. A definition CHANGE is validated against LIVE
		NODES: every active node on a board whose kind the old or new definition declares
		must fit the new resolution (type resolves, status known to its type's workflow). An
		incompatible node that no mapping covers REJECTS the whole call, naming the board,
		node key(s) and offending type/status — nothing is written. `migration` declares the
		repairs: [{ kind, types?:[{from,to}], statuses?:[{from,to}] }] — applied ONLY where a
		node's current value is invalid under the new resolution (a valid value is never
		rewritten); `to` must be valid under the new definition. When everything is mapped,
		the definition commits first and the repaired nodes are then rewritten as new
		temporal revisions per board, without FSM guards (the mapping is the sanctioned
		transition) — not one transaction with the definition, so re-check the named board if
		a concurrent-write error is thrown mid-rewrite. Returns { version (the new baseline),
		changed (false = identical resubmit, no new revision — skips the live-node check),
		migrated (nodes rewritten; 0 = none needed) }. Requires tasks:write.
		""")]
	public static async Task<MethodologyDefUpsertResult> MethodologyDefUpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("The whole methodology definition (structured document; see the tool description for the shape).")] MethodologyDefInput definition,
		[Description("Watermark baseline: the `version` from your last tasks_methodology_def_get; 0 = the project has no definition yet.")] long version = 0,
		[Description("Per-kind {from,to} type/status repairs for live nodes the change would strand; applied only where the current value is invalid under the new resolution.")] MethodologyMigrationInput[]? migration = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var ack = await tasks.DefineMethodologyAsync(projectKey, MethodologyWire.ParseDefinition(definition), version, MethodologyWire.ParseMigration(migration), ct);
		return new MethodologyDefUpsertResult(ack.Version, ack.Changed, ack.Migrated);
	}

	[McpServerTool(Name = "tasks_methodology_def_delete", Title = "Delete the project's methodology definition (revert to builtin presets)", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyDefDeleteResult))]
	[Description("""
		Delete the project's USER-DEFINED METHODOLOGY DEFINITION — every kind reverts to the
		built-in presets (a declared quartet kind to its preset, a custom kind to `simple`).
		`version` is the watermark baseline from your last tasks_methodology_def_get; a
		stale/future baseline is a clear conflict error naming the current version — re-read
		and retry. Validated against LIVE NODES before anything is written: every active node
		on a board whose kind the definition declares must fit the preset resolution it falls
		back to; an incompatible node REJECTS the whole call naming board/node/value (there is
		no migration on delete — move/close the offenders first, or change the definition via
		tasks_methodology_def_upsert with a migration instead). Deleting when no definition
		exists is an idempotent no-op (deleted:false). The revision history is kept (temporal
		soft-close) — the delete is itself a revision, not an erasure. Requires tasks:write.
		""")]
	public static async Task<MethodologyDefDeleteResult> MethodologyDefDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Watermark baseline: the `version` from your last tasks_methodology_def_get; 0 = delete the current revision regardless.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var ack = await tasks.DeleteMethodologyAsync(projectKey, version, ct);
		return new MethodologyDefDeleteResult(ack.Changed, ack.Version);
	}

	[McpServerTool(Name = "tasks_methodology_def_get", Title = "Get the project's methodology definition", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyDefGetResult))]
	[Description("""
		Return the project's USER-DEFINED METHODOLOGY DEFINITION — the stored process
		document (kinds/types/statuses/transitions as data), NOT the quartet board index
		(that is tasks_methodology_get). Defined=true → { name, kinds:[{ kind,
		quickAddAllowed, workflows:[{ types, initial, statuses:[{ slug, name, kind }],
		transitions:[{ from, to, requiresApproval, requiresReason, preconditionArtifact?,
		enforceApproval, checklist? }] }], linkConstraints?:[{ type, link, targetKind?,
		targetStatuses? }], effects?:[{ on, link, direction, set, onlyFrom? }] }], version
		(the baseline for
		tasks_methodology_def_upsert), created, updated, linkKinds?:[{ slug, description? }],
		tagAxes?:[{ namespace, description? }] } (the ?-marked lists are omitted when the
		definition declares none). Defined=false → the project has no definition of its own
		and runs on the built-in preset (`preset` names it) — an honest state, not an error.

		Pass `preset` (e.g. "quartet") to get that BUILT-IN preset RENDERED as a definition
		document (same shape, Defined=true, version 0, created/updated omitted) instead of the
		project's stored definition — a copyable STARTING POINT for a custom methodology: edit
		it, then install it via tasks_methodology_def_upsert (version 0). WARNING: this is a
		template, not an equivalent of the built-in quartet. The quartet kinds
		(intake/ideas/spec/work) declared in a DEFINITION LOSE their hardcoded BoardKind engine
		semantics — spec delivery roll-up, the ideaRef guard on spec writes, intake auto-close,
		and blocks auto-unblock are NOT reproduced from definition data (until engine v2). The
		statuses/transitions/gates and tag axes carry over; the cross-board process automation
		does not. Requires tasks:read.
		""")]
	public static async Task<MethodologyDefGetResult> MethodologyDefGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Render this built-in preset (e.g. \"quartet\") as a definition document (read-only template) instead of the project's stored definition. Omit for the stored definition.")] string? preset = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		// Preset copy: render the built-in preset as a definition template (version 0, no
		// created/updated — it was never stored). Same output shape as the stored definition.
		if (!string.IsNullOrWhiteSpace(preset))
			return MethodologyWire.ProjectDefinition(MethodologyPresets.RenderPresetDefinition(preset), version: 0, created: null, updated: null);
		var view = await tasks.GetMethodologyDefinitionAsync(projectKey, ct);
		if (view is null)
			return new MethodologyDefGetResult(Defined: false, Preset: BuiltinPreset);
		return MethodologyWire.ProjectDefinition(view.Definition, view.Version, view.Created, view.Updated);
	}

	[McpServerTool(Name = "tasks_methodology_guide", Title = "How to work this project's process (runtime-derived guide)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyGuideView))]
	[Description("""
		Return the AGENT ONBOARDING GUIDE for this project's process — how to work its
		boards — DERIVED AT RUNTIME from the project's methodology data: its own definition
		where one declares a kind (tasks_methodology_def_upsert), the built-in presets
		everywhere else. Call it when you start working a project's tasks and need the
		process rules; it is the runtime-derived replacement for hardcoded process docs, so
		it stays correct for user-defined kinds the docs never heard of. `markdown` covers,
		per effective kind: types (quick-add default marked), statuses grouped
		open/terminal, initial status, the transition map (collapsed to "free" when a block
		allows every move), the GATES as behavioral invariants (owner-only transitions the
		agent NEVER performs — marked enforced vs convention, reason-required moves,
		artifact:<slug> comment preconditions, pre-transition checklists), creation link
		requirements (specRef/blockedBy/ideaRef, incl. declared link targets), declared
		transition effects, tag axes (or free-form),
		and the relation-kind dictionary (process vs neutral vs project-declared).
		`invariants` is the same derivation machine-readable: [{ kind, rule:
		approval_gate|approval_gate_enforced|reason_required|precondition_artifact|
		checklist|transition_effect|link_constraint|tag_axes,
		detail }]. `source` = presets|definition|mixed; `definitionVersion` when a
		definition exists. Bounded (a handful of kinds) — no truncation. Requires tasks:read.
		""")]
	public static async Task<MethodologyGuideView> MethodologyGuideAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		return await tasks.GetMethodologyGuideAsync(projectKey, ct);
	}

	// The `preset` a definition-less project runs on: the built-in preset definitions
	// (simple + the methodology quartet kinds — MethodologyPresets).
	const string BuiltinPreset = MethodologyPresets.Name;

	// The definition wire mapping (ParseDefinition/ParseMigration/ProjectDefinition) lives in
	// MethodologyWire — shared with the admin methodology-editor page, so the editor's JSON is
	// shape-identical to the def_get/def_upsert documents.

	[McpServerTool(Name = "tasks_node_get", Title = "Get one node in full", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(NodeDetailView))]
	[Description("""
		Return ONE node of a board in FULL, addressed by `node` = its slug key OR its 32-hex
		NodeId (the same slug-or-NodeId convention as specRef/partOf). The answer carries the
		owning `board`, its `kind`, the part_of `ancestors` chain (root→parent), and the
		fully-enriched node: key, nodeId, parentNodeId/parentSlug/depth, status, type, title,
		the `body` (COMPLETE by default — this is the pointed full read; the uniform bodyLen knob still applies: 0 = no body, N>0 = the first N chars, -1 = full), priority, version, tags, links (`spec`,
		`blockedBy`; on a spec node `linkedTasks` + the computed `delivery`), plus `url` when
		includeUrl. `relations` is the EXHAUSTIVE two-way relation panel — one labelled group per
		non-empty kind×direction (children, blocks/blocked by, implements/linked tasks, idea/spec,
		issue/tasks, supersedes/superseded by), each target carrying its live status. An addressed read ignores terminality: a Done/Cancelled/deprecated node is
		returned like any other (no includeClosed needed). A node that doesn't exist on the
		board is a clear error, not an empty result. Use this instead of re-fetching a whole
		board when you need one node's full body. Requires tasks:read.
		""")]
	public static async Task<NodeDetailView> NodeGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board,
		[Description("The node's slug key on the board, or its 32-hex NodeId.")] string node,
		[Description("Body length knob (uniform contract): omitted = the FULL body (this is the pointed full read); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("Include an absolute `url` permalink to the node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		var detail = await tasks.GetNodeOnBoardAsync(projectKey, board, node, urlPrefix, ct);
		// Uniform bodyLen contract, default FULL (the pointed read); shape the wire body only.
		return detail with { Node = detail.Node with { Body = ModuleMcp.Body(detail.Node.Body, bodyLen, ModuleMcp.FullBody) ?? "" } };
	}

	[McpServerTool(Name = "tasks_search", Title = "Read plan nodes (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(TaskSearchResultView))]
	[Description("""
		THE read verb for plan nodes — one tool for both LISTING and SEARCH (list = search
		without `q`; replaces the former tasks.get). Nodes are FLAT (a single slug `key`);
		hierarchy is the part_of edge, surfaced as parentNodeId/parentSlug and a computed
		`depth` (0 = root) — build the tree from those. Every row carries its `board` plus
		key, nodeId, status, type, title, body, priority, version, renamedFrom, `tags`, `commits` (attached commit SHAs), and
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
		tasks_node_get — a miss or an ambiguous cross-board slug is a clear error, and an
		addressed terminal node is returned without includeClosed); `commit` = keep only nodes carrying that commit SHA (exact, or a >=7-hex prefix resolving a stored full sha).

		SORT: `sort` = {by: priority|created|updated|title|relevance, desc?}. Without `q`
		the default is priority (asking for relevance is an error); with `q` the default is
		relevance, and an explicit sort reorders WITHIN the relevance-selected set (`desc`
		is ignored for relevance). `limit` caps the rows (with `q` it defaults to 20, 0 =
		no cap; a listing is unbounded by default — the output budget still applies).

		With `q` each row carries `score` (the fused, rank-based relevance) and `retriever`
		("lexical" = lexically confirmed, "semantic" = surfaced by the vector leg alone,
		"exact" = an exact slug match); a semantic-only hit below the relevance floor is
		dropped, so `limit` is a CEILING, not a plan (a query can return fewer rows). COMMENTS
		are searched too (lexical leg): a comment match returns its OWNER node row marked
		`matchedIn:"comment"` (spec tasks-search-comments); a plain node match leaves it null.
		Query
		rows are LEAN (spec search-lean-rows): identity/title/snippet/status/tags/version +
		score/retriever only — links/delivery/parent/commits/priority are dropped and ride the
		listing mode (no q) or tasks_node_get (version stays as the CAS baseline for an
		upsert-after-find, tags aid selection).

		PROJECTION: `groupBy` = an ORDERED, comma-separated list of tag namespaces (e.g.
		"area" or "area,concern") returns the tag-bucket view instead of rows (`groups`
		nested in that order, "(none)" for untagged, each with a delivery roll-up); needs
		`board` and does NOT combine with `q` (a projection is a view, not a ranking).

		Bodies follow the uniform `bodyLen` knob: omitted = a ~240-char snippet (the compact
		listing default), 0 = no body, N>0 = the first N chars ("…" when cut), -1 = full body — or fetch one full body via
			tasks_node_get. The response has a HARD OUTPUT BUDGET
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
		[Description("Address specific nodes: slugs and/or 32-hex NodeIds, mixed (resolved like tasks_node_get; terminal nodes included).")] string[]? keys = null,
		[Description("Include terminal/closed nodes in a listing (search covers the open set only).")] bool includeClosed = false,
		[Description("Sort order: {by: priority|created|updated|title|relevance, desc?}. Default: priority (listing) / relevance (with q).")] SortInput? sort = null,
		[Description("Tag PROJECTION instead of rows: an ordered, comma-separated list of tag namespaces (e.g. \"area,concern\"). Needs board; not with q.")] string? groupBy = null,
		[Description("Body length knob (uniform contract): omitted = a ~240-char snippet (the compact listing default — fetch a full body with tasks_node_get or bodyLen:-1); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("Max rows returned. Default: unbounded listing / 20 with q (0 = no cap).")] int? limit = null,
		[Description("Include an absolute `url` permalink to each node's detail page (off by default).")] bool includeUrl = false,
		[Description("Reverse commit lookup: keep only nodes carrying this commit SHA — an exact match, or a >=7-hex prefix that resolves a stored full sha. Applies in both modes.")] string? commit = null,
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
			Filter = new TaskNodeFilter(board, under, status, keys, includeClosed, commit),
			Sort = ParseSort(sort),
			Limit = limit ?? (hasQuery ? DefaultSearchLimit : 0),
			BodyLen = 0, // request FULL bodies; the adapter applies the uniform bodyLen contract below
		}, urlPrefix, ct);

		// Response budget (MCP-adapter-only): the adapter shapes each body per the uniform bodyLen
		// knob (default a ~240-char snippet) THEN measures the wire form, prefix-cuts, marks — never silent.
		var rows = res.Hits.Select(h => SearchRow(h, bodyLen, lean: hasQuery)).ToList();
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
		"`limit`, `groupBy` (keys-only tag projection), or tasks_node_get for one full node.";

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
	// LEAN when the caller has a query (spec search-lean-rows): a relevance row carries only
	// what picks the entity — identity/title/snippet/status/tags/version + score/retriever —
	// while the enrichment (parent/depth/delivery/spec/links/commits/priority) is nulled →
	// omitted on the wire; completeness comes from listing mode or tasks_node_get. Version is
	// kept as the CAS baseline for upsert-after-find (same as memory_search rows) and Tags aid
	// selection. Listing mode (no query) keeps the full row unchanged.
	static TaskSearchNodeView SearchRow(TaskSearchHit h, int? bodyLen, bool lean)
	{
		var n = h.Node;
		return new TaskSearchNodeView(
			Key: n.Key,
			NodeId: n.NodeId,
			Board: h.Board,
			ParentNodeId: lean ? null : n.ParentNodeId,
			ParentSlug: lean ? null : n.ParentSlug,
			Depth: lean ? null : (int?)n.Depth,
			Status: n.Status,
			Type: n.Type,
			Title: n.Title,
			// Uniform bodyLen contract, default a ~240-char snippet (compact listing); null
			// (bodyLen:0) is omitted by the serializer.
			Body: ModuleMcp.Body(n.Body, bodyLen, ModuleMcp.DefaultSnippet),
			Commits: lean ? null : n.Commits,
			Priority: lean ? null : (long?)n.Priority,
			Delivery: lean ? null : n.Delivery,
			Spec: lean ? null : n.Spec,
			BlockedBy: lean ? null : n.BlockedBy,
			LinkedTasks: lean ? null : n.LinkedTasks,
			Supersedes: lean ? null : n.Supersedes,
			RenamedFrom: lean ? null : (n.RenamedFrom is { Count: > 0 } rf ? rf : null),
			Tags: n.Tags,
			Version: n.Version,
			Url: n.Url,
			// Per-row relevance provenance (query mode; null → omitted in listing mode).
			Score: h.Score is { } s ? Math.Round(s, 6) : null,
			Retriever: h.Retriever,
			// Relevance provenance — survives the lean cut like Score/Retriever.
			MatchedIn: h.MatchedIn);
	}

	// Split a comma-separated groupBy ("area,concern") into the ordered dimension list the
	// service expects; blanks dropped, order and dups preserved (service validates namespaces).
	static string[] ParseGroupBy(string groupBy) =>
		groupBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	[McpServerTool(Name = "tasks_upsert", Title = "Upsert plan nodes", UseStructuredContent = true, OutputSchemaType = typeof(UpsertResultView))]
	[Description("""
		Declarative PATCH per node (omitted field = unchanged; tags: [] clears, omit leaves
		as-is) — a temporal upsert of plan nodes. Requires tasks:write.

		Each node has a FLAT `key` — a single slug [a-z][a-z0-9_-]{0,99} (no '/'; the old
		l1/l2/l3 path is gone). Nesting is the `partOf` field: a parent slug (on this board)
		or a NodeId — null omits it, "" detaches to a root. A node may carry multiple parents'
		worth of grouping via `tags` (an array of "namespace:value", namespaces area|concern;
		[] clears, omit leaves as-is). Give each node a `title` and `body` (GFM markdown —
		renders as formatted text: use ## headings, real newlines (not \\n literals, not
		==headings==); numbered lists as `1.`; markdown is client-rendered via
		marked+DOMPurify (gfm:true, breaks:true — a bare newline becomes a <br>)). Other
		fields: status (slug — see tasks_workflow), type (feature|bug|chore on work boards;
		chore = spec-less engineering hygiene), specRef (the spec node the work task
		implements, as its slug on the linked spec board or a NodeId — REQUIRED for a new
		feature/bug), ideaRef (ON A SPEC BOARD: the NodeId of the
		`accepted` idea this create/change is made under — REQUIRED for every spec node; becomes
		the idea_spec edge), blockedBy (the blocking node as its slug on THIS board or a
		NodeId — the same slug-or-NodeId convention as specRef/partOf), supersedes
		(a slug|NodeId this node replaces — the old one is moved to its terminal-cancel),
		commits? (an ARRAY of commit SHAs — hex, 7..40 chars; null omits, [] clears, a list
		REPLACES the node's full commit set, same PATCH semantics as tags), priority? (sparse
		int, lower first), version (WATERMARK baseline: pass the
		board `currentVersion` from your last read OR the node's own version — both are valid; 0 =
		new; a version above this board's cursor is rejected as a wrong-scope baseline). The guard
		is about PAYLOAD, not version arithmetic: a payload identical to the node's current state
		no-ops even on an old baseline (an FSM effect or another writer already did it — no retry
		needed), and an old baseline conflicts ONLY when the node semantically moved after your
		read — attachment writes and other bookkeeping bumps auto-resolve (their keys land in
		`autoResolved[]`). Rename via prevKey. A cold call auto-creates the board.

		To DELETE a node, pass { key, deleted:true } (optional version baseline; 0 = delete
		regardless) — the node is soft-closed (history kept), its edges and tags are closed, and
		its key appears in `removed[]`. A node with active part_of children is refused (Rejected
		conflict) — delete the children first, or the whole subtree in one call. deleted cannot
		combine with prevKey. Spec-node deletes need no ideaRef (erasing junk is not a spec
		change — retiring a real requirement stays `deprecated`).

		Returns the pure write-ack { applied, currentVersion, inserted, closed, conflicts[],
		added[], updated[], removed[], autoResolved[] }. `applied` is the SINGLE source of truth:
		when it is FALSE
			nothing was written — `conflicts[]` explains every rejected key (its baseline vs the
			active version, plus a reason for a guard refusal; a Stale conflict also carries
			`changedFields` — THIS node's payload fields that moved past your baseline, so rebase
			on those facts instead of blindly resubmitting) and added/updated/removed are EMPTY;
			re-read via tasks_delta (or tasks_search) to rebase, then resubmit. When `applied` is
			TRUE the echo covers ONLY this call: added/updated/removed
		carry the call's own nodes plus nodes its cascade effects touched (a `supersedes`
		target obsoleted, a deleted subtree, an unblocked task) — never other writers'
		history, and there is no cursor parameter on a write. added/updated carry the node
		(key, nodeId, status, type, title, commits[], priority, version); `body` follows the
		uniform bodyLen knob (omitted here = NO body, a compact ack; 0 = no body; N>0 = the first
		N chars, "…" when cut; -1 = full body). `currentVersion` is the board-wide cursor: for a full delta
		since a cursor (everything changed by anyone — rebase/merge/catch-up), call
		tasks_delta with it as `sinceVersion`.
		""")]
	public static async Task<UpsertResultView> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board,
		[Description("Array of node objects: flat `key`, optional `partOf` (parent slug|NodeId), `tags` (array of ns:value), `commits` (array of hex SHAs), `specRef` (spec slug|NodeId), `ideaRef`, `blockedBy` (blocker slug|NodeId), `supersedes`, status/type/title/body/priority/version, and `prevKey` to rename.")] PlanNodeInput[] nodes,
		[Description("Body length knob (uniform contract): omitted = NO body (the compact ack default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("Include an absolute `url` permalink to each returned node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// The SESSION key's scopes decide the actor capability: tasks:approve elevates the
		// write past methodology-ENFORCED approval gates (enforceApproval transitions).
		var actor = ModuleMcp.HasScope(http, ApiKeyScopes.TasksApprove) ? TasksActor.Approver : TasksActor.None;
		var patches = ParseNodePatches(nodes);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.UpsertAsync(projectKey, board, patches, actor, ct), urlPrefix, bodyLen);
	}

	[McpServerTool(Name = "tasks_delta", Title = "Plan delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(UpsertResultView))]
	[Description("Return nodes added/updated/removed since `sinceVersion` (no writes) — THE cursor/catch-up surface (a tasks_upsert ack echoes only its own call; pass its `currentVersion` here for the full board delta). Bodies follow the uniform bodyLen knob (compact by default). Requires tasks:read.")]
	public static async Task<UpsertResultView> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, long sinceVersion,
		[Description("Body length knob (uniform contract): omitted = NO body (compact default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("Include an absolute `url` permalink to each returned node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.DeltaAsync(projectKey, board, sinceVersion, ct), urlPrefix, bodyLen);
	}

	[McpServerTool(Name = "tasks_workflow", Title = "Board workflow (kinds/statuses/transitions)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(WorkflowView))]
	[Description("Return the workflow for a board: its kind plus `workflows` — one block per DISTINCT state machine, each carrying `types` (every type slug sharing that FSM; e.g. feature|bug|chore on a work board are one block), the initial status, statuses (slug, name, kind=open|terminalok|terminalcancel) and transitions (from, to, requiresApproval, requiresReason, preconditionArtifact? — a comment-artifact tag the node must carry before the transition). A kind the project's methodology definition declares (tasks_methodology_def_upsert) resolves from the definition; other kinds report the built-in preset. Use this to learn the legal types/statuses before tasks_upsert. Requires tasks:read.")]
	public static async Task<WorkflowView> WorkflowAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		// Grouping (identical FSMs into one block) and catalog-vs-definition resolution
		// happen in the service; this adapter only shapes the wire.
		var view = await tasks.GetBoardWorkflowAsync(projectKey, board, ct);
		return new WorkflowView(
			Kind: view.Kind,
			Workflows: view.Workflows.Select(g => new WorkflowGroupView(
				Types: g.Types.ToList(),
				Initial: g.Workflow.Initial,
				Statuses: g.Workflow.Statuses.Select(s => new WorkflowStatusView(s.Slug, s.Name, s.Kind.ToString().ToLowerInvariant())).ToList(),
				Transitions: g.Workflow.Transitions.Select(t => new WorkflowTransitionView(t.From, t.To, t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact)).ToList())).ToList());
	}

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

	static UpsertResultView Serialize(UpsertOutcome o, string? urlPrefix = null, int? bodyLen = null)
	{
		var r = o.Result;
		return new UpsertResultView(
			Applied: r.Applied,
			CurrentVersion: r.CurrentVersion,
			Kind: o.Kind,
			Inserted: r.Inserted,
			Closed: r.Closed,
			Conflicts: r.Conflicts.Select(c => new UpsertConflictView(c.Key, c.Kind.ToString(), c.BaselineVersion, c.ActiveVersion, c.Reason, c.ChangedFields)).ToList(),
			Added: r.Added.Select(n => NodeDto(n, urlPrefix, bodyLen)).ToList(),
			Updated: r.Updated.Select(n => NodeDto(n, urlPrefix, bodyLen)).ToList(),
			Removed: r.Removed.ToList(),
			AutoResolved: r.AutoResolved.ToList());
	}

	// Delta projection of a node (no links/delivery/tags — that's tasks_search). camelCased by the
	// serializer; `body` follows the uniform bodyLen contract with a NoBody default (a compact echo).
	static PlanNodeDelta NodeDto(PlanNode n, string? urlPrefix = null, int? bodyLen = null) => new(
		Key: n.Key,
		NodeId: n.NodeId,
		Status: n.Status,
		Type: n.Type,
		Title: n.Name,
		Body: ModuleMcp.Body(n.Body, bodyLen, ModuleMcp.NoBody),
		Commits: n.Commits,
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
				// Commits: null = omit (don't touch); a non-null list (incl. empty) REPLACES the
				// node's full commit set — same semantics as Tags.
				Commits = n.Commits,
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
