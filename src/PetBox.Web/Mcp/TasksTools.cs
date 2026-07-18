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
	[Description("CREATE one named task board in a project for a single `kind` (simple|classic|spec|ideas|intake|work, default simple — plus any kind a methodology instance's rules or the project's utility layer declare). Does not store a template and does not provision a full methodology (that is tasks_methodology_create). `kind` drives the workflow — call tasks_workflow for valid types/statuses/transitions; an unknown kind is rejected naming the valid ones. `methodologyInstance` names the WORLD this board belongs to (spec methodology-utility-kinds: a board is a member of exactly one) — an instance name, or the reserved sentinel \"$utility\" for the project's utility layer (always legal, independent of how many instances exist). Required once the project has any methodology instance — board_create without one is then rejected. `specBoard` (work boards only) names the spec board this board's tasks link into. Requires tasks:write.")]
	public static async Task<BoardCreatedResult> BoardCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, [LogArg] string board, string? kind = null, string? description = null, string? specBoard = null,
		[Description("The board's world: a methodology instance name, or \"$utility\" for the project's utility layer. Required when the project has any instance.")] string? methodologyInstance = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var meta = await tasks.CreateBoardAsync(projectKey, board, kind, description, specBoard, methodologyInstance, ct);
		return new BoardCreatedResult(meta.ProjectKey, meta.Name, meta.Kind, meta.Description, meta.SpecBoard, meta.CreatedAt, meta.MethodologyInstance);
	}

	[McpServerTool(Name = "tasks_board_adopt", Title = "Adopt/move a board into a methodology instance, or release it to the utility layer", UseStructuredContent = true, OutputSchemaType = typeof(BoardAdoptResult))]
	[Description("Move (adopt) an existing board into a methodology instance, OR release it into the project's utility layer (spec methodology-utility-kinds) by passing the reserved sentinel \"$utility\" — a board's world changes exactly once per call, never to \"no world\". Enforces process-role singleton (≤1 open board per singleton kind) INSIDE the target (the instance, or the utility bucket). A named instance target must be open; releasing to \"$utility\" rejects a kind the utility layer does not declare and no builtin resolves — declare it first (tasks_methodology_utility_upsert). GOVERNANCE: this re-points an existing board's live nodes at another world's rules — requires tasks:write AND methodology:write.")]
	public static async Task<BoardAdoptResult> BoardAdoptAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board,
		[Description("Target: a methodology instance name, or \"$utility\" to release the board into the project's utility layer.")] string methodologyInstance,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// Adopting an EXISTING board re-points its EXISTING nodes at another instance's
		// rules — the criterion "changes the rules for existing nodes" is met even though
		// no rules document is edited here. Governance-gated.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		var meta = await tasks.AdoptBoardAsync(projectKey, board, methodologyInstance, ct);
		return new BoardAdoptResult(meta.Name, meta.Kind, meta.MethodologyInstance);
	}

	[McpServerTool(Name = "tasks_board_set_spec", Title = "Set a work board's spec board", UseStructuredContent = true, OutputSchemaType = typeof(BoardSetSpecResult))]
	[Description("Set (or clear, when specBoard is omitted) the spec board a work board's tasks link into. The target must be a spec board. Makes the work->spec link explicit. GOVERNANCE: re-targets or severs the edge that link-constraints and delivery resolve through for every node already on the board — requires tasks:write AND methodology:write.")]
	public static async Task<BoardSetSpecResult> BoardSetSpecAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, string? specBoard = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// Rewires the work->spec edge of an EXISTING board: it re-targets (or, when specBoard is
		// omitted, SEVERS) the link the rules' link-constraints and delivery roll-up resolve
		// through, for every node already on the board. This one meets even the narrow original
		// criterion — I had excluded it before on the reading that it edits no rules document.
		// It does not need to: it changes what the rules MEAN for existing nodes. Gated.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
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
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await tasks.ListBoardsAsync(projectKey, ct);
		return new BoardListResult(list.Select(b => new BoardRow(b.Name, b.Kind, b.Description, b.SpecBoard, b.CreatedAt, b.ClosedAt != null, b.MethodologyInstance)).ToList());
	}

	[McpServerTool(Name = "tasks_board_delete", Title = "Delete a task board", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(BoardDeletedResult))]
	[Description("Delete a task board and its nodes. GOVERNANCE: removes a process role from a live methodology instance and destroys its nodes — requires tasks:write AND methodology:write.")]
	public static async Task<BoardDeletedResult> BoardDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// Removes a process ROLE from a live instance and destroys its nodes with it. The most
		// irreversible governance act on the surface — gated.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		return new BoardDeletedResult(await tasks.DeleteBoardAsync(projectKey, board, ct));
	}

	[McpServerTool(Name = "tasks_board_close", Title = "Close (archive) a task board", UseStructuredContent = true, OutputSchemaType = typeof(BoardClosedResult))]
	[Description("Close a board: it rejects further writes (so agents stop writing to it by inertia) but stays readable; history is kept. Reopen with tasks_board_reopen. GOVERNANCE: retires a process role of a live methodology instance for everyone — requires tasks:write AND methodology:write.")]
	public static async Task<BoardClosedResult> BoardCloseAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// A board is not "just data": in a methodology instance it IS a process role (the spec
		// board, the work board). Closing one retires that role for everyone — close the spec
		// board and the rules' own "a work feature must link a spec node" becomes unsatisfiable,
		// which halts the process without editing one line of rules. Governance act — gated.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		return new BoardClosedResult(await tasks.SetClosedAsync(projectKey, board, true, ct));
	}

	[McpServerTool(Name = "tasks_board_reopen", Title = "Reopen a closed task board", UseStructuredContent = true, OutputSchemaType = typeof(BoardReopenedResult))]
	[Description("Reopen a closed board so it accepts writes again. GOVERNANCE: undoes a deliberate governance freeze — same scope as tasks_board_close, requires tasks:write AND methodology:write.")]
	public static async Task<BoardReopenedResult> BoardReopenAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// The inverse of a gated act must be gated too, or the gate is one-way theatre: a
		// deliberate governance freeze could be undone by any tasks:write key. Same scope as
		// board_close, on purpose.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		return new BoardReopenedResult(await tasks.SetClosedAsync(projectKey, board, false, ct));
	}

	// ---- methodology instances (named live process automata) ----
	// Unified dictionary: template (inert document) + methodology (instance) verbs
	// create / list / get / close, plus rules_get / rules_upsert for live rules edit.

	[McpServerTool(Name = "tasks_methodology_create", Title = "Create a methodology instance", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyInstanceCreateResult))]
	[Description("""
		Create a NAMED methodology INSTANCE in one act from an EXPLICIT source — no silent
		quartet default. Sources: `builtin` (sourceKey = quartet|classic|simple), `template`
		(sourceKey = stored/builtin template key), `instance` (sourceKey = existing instance
		name — snapshot its rules). Provisions instance rules + one board per kind in the
		source definition; process-role singleton applies INSIDE the new instance (a second
		instance may reuse the same process-role kinds). Template write alone never creates
		boards — only this call does. GOVERNANCE: this authors a LIVE rules document —
		requires tasks:write AND methodology:write.
		""")]
	public static async Task<MethodologyInstanceCreateResult> MethodologyCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Instance name (slug ^[a-z][a-z0-9_-]{0,99}$).")] string name,
		[Description("Source kind: builtin | template | instance.")] string source,
		[Description("Source key: builtin slug, template key, or source instance name.")] string sourceKey,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// Creating an instance AUTHORS a live rules document. On its own it governs only
		// the boards it provisions — but create+board_adopt is the composed bypass of
		// rules_upsert (mint your own rules, then pull existing boards under them), so the
		// criterion binds here too and both halves must be gated or neither is.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		var ack = await tasks.CreateMethodologyInstanceAsync(projectKey, name, source, sourceKey, ct);
		return new MethodologyInstanceCreateResult(
			ack.Name, ack.Changed, ack.Closed, ack.Version,
			ack.Boards.Select(b => new MethodologyInstanceBoardView(b.Name, b.Kind, b.Closed, b.SpecBoard)).ToList());
	}

	[McpServerTool(Name = "tasks_methodology_list", Title = "List methodology instances", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyInstanceListResult))]
	[Description("""
		List methodology INSTANCES in the project as a compact INDEX: name, closed, kinds,
		boards (name/kind/closed/specBoard), status histogram counts — no node bodies.
		Requires tasks:read.
		""")]
	public static async Task<MethodologyInstanceListResult> MethodologyListAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var items = await tasks.ListMethodologyInstancesAsync(projectKey, ct);
		return new MethodologyInstanceListResult(items.Select(ProjectInstance).ToList());
	}

	[McpServerTool(Name = "tasks_methodology_get", Title = "Get a methodology instance", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyInstanceGetResult))]
	[Description("""
		Return ONE methodology INSTANCE by `name` as a compact INDEX (identity, boards,
		status histogram counts, computed summary — no node bodies). Found=false on miss
		(not an error). For every instance use tasks_methodology_list. Requires tasks:read.
		""")]
	public static async Task<MethodologyInstanceGetResult> MethodologyGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Instance name (slug).")] string name,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var view = await tasks.GetMethodologyInstanceAsync(projectKey, name, ct);
		if (view is null)
			return new MethodologyInstanceGetResult(Found: false, Name: name);
		return new MethodologyInstanceGetResult(Found: true, Instance: ProjectInstance(view));
	}

	[McpServerTool(Name = "tasks_methodology_close", Title = "Close a methodology instance", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyInstanceCloseResult))]
	[Description("""
		Close a NAMED methodology INSTANCE whole: marks the instance closed and closes every
		member board. Closed boards stay readable (history/search) but reject new writes.
		Idempotent when already closed. GOVERNANCE: retires a whole live process — requires
		tasks:write AND methodology:write.
		""")]
	public static async Task<MethodologyInstanceCloseResult> MethodologyCloseAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Instance name (slug) to close.")] string name,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// Retires a whole live process: the instance AND every member board stop accepting
		// writes. Changes no rules document, so the narrow "changes the rules for existing
		// nodes" test misses it — but it is a governance act over an EXISTING process, which
		// is the criterion. Gating rules_upsert while leaving this open would be a hole: you
		// cannot rewrite the process, but you could retire it wholesale.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		var ack = await tasks.CloseMethodologyInstanceAsync(projectKey, name, ct);
		return new MethodologyInstanceCloseResult(
			ack.Name, ack.Changed, ack.Closed, ack.Version,
			ack.Boards.Select(b => new MethodologyInstanceBoardView(b.Name, b.Kind, b.Closed, b.SpecBoard)).ToList());
	}

	[McpServerTool(Name = "tasks_methodology_active_get", Title = "Get the project's active methodology instance pointer", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyActiveGetResult))]
	[Description("""
		Return the project's explicit ACTIVE methodology instance pointer (spec
		methodology-active-instance) — the instance DEFAULT surfaces (UI, MCP verbs called
		without an explicit instance, tasks_methodology_guide with no `name`) resolve through
		when set. NEVER overrides board membership — a board's own methodology instance
		(tasks_board_create's methodologyInstance) always wins regardless of what is active
		here. `name` is null when no pointer is set: resolution then falls back to the single
		open instance when there is exactly one, or an explicit "no active instance" guide
		otherwise (never a silent merge). `version` is the CAS baseline for
		tasks_methodology_set_active. Requires tasks:read.
		""")]
	public static async Task<MethodologyActiveGetResult> MethodologyActiveGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var view = await tasks.GetActiveMethodologyInstanceAsync(projectKey, ct);
		return new MethodologyActiveGetResult(view.Name, view.Version);
	}

	[McpServerTool(Name = "tasks_methodology_set_active", Title = "Set (or clear) the project's active methodology instance", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyActiveSetResult))]
	[Description("""
		Set the project's explicit ACTIVE methodology instance pointer, or CLEAR it (omit/null
		`name`) — spec methodology-active-instance. Controls DEFAULTS only: UI, MCP verbs
		without an explicit instance, and tasks_methodology_guide with no `name` resolve
		through this pointer when set. NEVER controls board membership — a board that belongs
		to instance X always resolves X's rules even while Y is active (board membership
		always wins). The pointer MUST reference an OPEN instance: naming a missing or closed
		instance is rejected, nothing is written — close it first or pick another. `version`
		is the watermark baseline from tasks_methodology_active_get (0 = no prior read).
		GOVERNANCE: tasks_methodology_guide (called with no `name`) resolves through this
		pointer, and the guide is the only control that exists for CONVENTION gates — moving
		it changes what every agent is taught the process IS. Requires tasks:write AND
		methodology:write.
		""")]
	public static async Task<MethodologyActiveSetResult> MethodologySetActiveAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Instance name (slug) to make active; omit/null to clear the pointer.")] string? name,
		[Description("Watermark baseline: version from tasks_methodology_active_get; 0 = no prior read.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// GOVERNANCE (spec methodology-write-scope, owner-widened criterion). Board membership
		// always wins, so no node's ENFORCEMENT changes — under the narrow original criterion
		// ("changes the rules for existing nodes") this verb would walk free. It is gated under
		// the WIDENED criterion, and the reason is not cosmetic:
		//
		// tasks_methodology_guide with no `name` resolves through this pointer, and the guide is
		// the ONLY control that exists for CONVENTION gates — a non-enforced approval_gate is,
		// by definition, one the server does NOT block; the sole thing stopping an agent from
		// self-approving is that the guide told it not to. Flipping the pointer at an instance
		// whose transitions lack RequiresApproval deletes those approval_gate invariants from
		// the guide outright (MethodologyGuide derives them from the RESOLVED instance's
		// structure). That is a complete bypass of every convention gate, by one pointer write.
		// Gating board_close while leaving this open would be incoherent.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		var ack = await tasks.SetActiveMethodologyInstanceAsync(projectKey, name, version, ct);
		return new MethodologyActiveSetResult(ack.Name, ack.Changed, ack.Version);
	}

	static MethodologyInstanceViewResult ProjectInstance(MethodologyInstanceView v) => new(
		v.Name, v.Closed, v.Version, v.Created, v.Updated, v.ClosedAt, v.DefinitionName, v.Kinds,
		v.Boards.Select(b => new MethodologyInstanceBoardView(b.Name, b.Kind, b.Closed, b.SpecBoard)).ToList(),
		v.Counts);

	[McpServerTool(Name = "tasks_methodology_rules_get", Title = "Get a methodology instance's rules document", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyInstanceRulesGetResult))]
	[Description("""
		Return the RULES DOCUMENT of one methodology INSTANCE by name — the live process
		document (kinds/types/statuses/transitions) that member boards resolve against,
		plus the version baseline for tasks_methodology_rules_upsert. Same document shape as
		tasks_methodology_template_get (kinds/workflows/linkKinds/tagAxes). Found=false on
		miss (not an error). Closed instances still return their last rules (read-only —
		rules_upsert rejects closed). Requires tasks:read.
		""")]
	public static async Task<MethodologyInstanceRulesGetResult> MethodologyRulesGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Instance name (slug).")] string name,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var view = await tasks.GetMethodologyInstanceRulesAsync(projectKey, name, ct);
		if (view is null)
			return new MethodologyInstanceRulesGetResult(Found: false, Name: name);
		var doc = MethodologyWire.ProjectDefinition(view.Definition, view.Version, view.Created, view.Updated);
		return new MethodologyInstanceRulesGetResult(
			Found: true,
			Name: view.Name,
			Closed: view.Closed,
			DefinitionName: doc.Name,
			Kinds: doc.Kinds,
			Version: view.Version,
			Created: view.Created,
			Updated: view.Updated,
			LinkKinds: doc.LinkKinds,
			TagAxes: doc.TagAxes,
			StrictMode: doc.StrictMode);
	}

	[McpServerTool(Name = "tasks_methodology_rules_upsert", Title = "Edit a live methodology instance's rules (with migration)", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyInstanceRulesUpsertResult))]
	[Description("""
		Replace the RULES of a LIVE methodology INSTANCE with optimistic concurrency and
		declarative live-node migration. Does NOT mutate templates or other instances.
		`name` addresses the instance; `version` is the watermark baseline from
		tasks_methodology_rules_get (a stale/future baseline is a clear conflict).
		`definition` is the same document shape as tasks_methodology_template_upsert. A CHANGE
		is validated against LIVE NODES on this instance's open member boards only: every
		active node whose board kind the old or new rules declare must fit the new resolution.
		An incompatible node that no mapping covers REJECTS the whole call, naming
		board/node/value — nothing is written. `migration` declares the repairs:
		[{ kind, types?:[{from,to}], statuses?:[{from,to}] }] — applied ONLY where a node's
		current value is invalid under the new resolution (a valid value is never rewritten).
		Closed instances reject the write. Returns { name, version, changed, migrated }.
		GOVERNANCE: this changes the rules that already govern EXISTING nodes — requires
		tasks:write AND methodology:write. (Inert templates do not: see template_upsert.)
		""")]
	public static async Task<MethodologyInstanceRulesUpsertResult> MethodologyRulesUpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Instance name (slug) whose rules to replace.")] string name,
		[Description("The whole methodology rules document (same shape as tasks_methodology_template_upsert).")] MethodologyDefInput definition,
		[Description("Watermark baseline: the `version` from your last tasks_methodology_rules_get.")] long version = 0,
		[Description("Per-kind {from,to} type/status repairs for live nodes on this instance the change would strand.")] MethodologyMigrationInput[]? migration = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// The paradigm case: rewrites the rules of a LIVE instance and migrates the live
		// nodes on its member boards. Governance-gated.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		var def = MethodologyWire.ParseDefinition(definition);
		var ack = await tasks.DefineMethodologyInstanceRulesAsync(
			projectKey, name, def, version, MethodologyWire.ParseMigration(migration), ct);
		return new MethodologyInstanceRulesUpsertResult(ack.Name, ack.Version, ack.Changed, ack.Migrated);
	}

	[McpServerTool(Name = "tasks_methodology_utility_get", Title = "Get the project's utility-kind layer", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyUtilityGetResult))]
	[Description("""
		Return the project's UTILITY LAYER of kinds (spec methodology-utility-kinds) — kinds
		homed on the project rather than inside a methodology instance, so they exist
		independently of the active methodology and survive its switch. Same document shape
		as tasks_methodology_rules_get/template_get (kinds/workflows/linkConstraints/effects/
		linkKinds/tagAxes), plus the version baseline for tasks_methodology_utility_upsert. A
		board with no instance membership (methodologyInstance omitted, or the reserved
		"$utility" sentinel on tasks_board_create/tasks_board_adopt) resolves its kind against
		this document; an undeclared kind falls back to the built-in presets (simple|classic).
		Found=false when the project has never defined a utility layer (everything then
		resolves from presets alone) — not an error. Requires tasks:read.
		""")]
	public static async Task<MethodologyUtilityGetResult> MethodologyUtilityGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var view = await tasks.GetMethodologyDefinitionAsync(projectKey, ct);
		if (view is null)
			return new MethodologyUtilityGetResult(Found: false);
		var doc = MethodologyWire.ProjectDefinition(view.Definition, view.Version, view.Created, view.Updated);
		return new MethodologyUtilityGetResult(
			Found: true,
			DefinitionName: doc.Name,
			Kinds: doc.Kinds,
			Version: view.Version,
			Created: view.Created,
			Updated: view.Updated,
			LinkKinds: doc.LinkKinds,
			TagAxes: doc.TagAxes);
	}

	[McpServerTool(Name = "tasks_methodology_utility_upsert", Title = "Edit the project's utility-kind layer (with migration)", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyUtilityUpsertResult))]
	[Description("""
		Replace the project's UTILITY LAYER of kinds (spec methodology-utility-kinds) with
		optimistic concurrency and declarative live-node migration — the same document shape
		and CAS discipline as tasks_methodology_rules_upsert, scoped to the project instead of
		one instance. `version` is the watermark baseline from tasks_methodology_utility_get (a
		stale/future baseline is a clear conflict). A CHANGE is validated against LIVE NODES on
		every board with NO instance membership (methodologyInstance omitted, or "$utility"):
		every active node whose board kind the old or new document declares must fit the new
		resolution; an incompatible node with no `migration` entry REJECTS the whole call naming
		board/node/value — nothing is written. Declaring a kind here does NOT move any board —
		use tasks_board_adopt(methodologyInstance:"$utility") to release an existing board out
		of its instance once the kind it needs is declared. GOVERNANCE: this changes the rules
		that already govern existing utility-homed nodes — requires tasks:write AND
		methodology:write.
		""")]
	public static async Task<MethodologyUtilityUpsertResult> MethodologyUtilityUpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("The whole utility-layer document (same shape as tasks_methodology_rules_upsert).")] MethodologyDefInput definition,
		[Description("Watermark baseline: the `version` from your last tasks_methodology_utility_get (0 = create).")] long version = 0,
		[Description("Per-kind {from,to} type/status repairs for live utility-homed nodes the change would strand.")] MethodologyMigrationInput[]? migration = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// Same governance posture as rules_upsert: this rewrites the rules that already
		// govern every live utility-homed node. Gated.
		ModuleMcp.AssertScope(http, ApiKeyScopes.MethodologyWrite);
		var def = MethodologyWire.ParseDefinition(definition);
		var ack = await tasks.DefineMethodologyAsync(projectKey, def, version, MethodologyWire.ParseMigration(migration), ct);
		return new MethodologyUtilityUpsertResult(ack.Version, ack.Changed, ack.Migrated);
	}

	[McpServerTool(Name = "tasks_methodology_describe", Title = "Describe one methodology primitive (prose only, by natural key)", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyDescribeResult))]
	[Description("""
		Set (or clear) the free-form Description of ONE primitive of a LIVE methodology
		INSTANCE's rules — a kind, status, transition, effect, constraint, linkKind or
		tagAxis — addressed by its NATURAL KEY, not a version-CAS whole-document replace
		(that stays tasks_methodology_rules_upsert, for STRUCTURE). This verb only ever
		replaces one Description string; it can never add/remove/reorder a kind, block,
		status, transition, effect or constraint.

		`primitive` selects the natural key shape:
		- kind: { kind }
		- status: { kind, type, slug } — `type` names any ONE type slug of the owning
		  workflow block (a block is shared by every type in it; any of its types
		  disambiguates it).
		- transition: { kind, type, from, to }
		- effect: { kind, on, link, direction, onLeave? } (onLeave defaults false)
		- constraint: { kind, type, link }
		- linkKind: { slug }
		- tagAxis: { namespace }

		`description` is the new prose; pass "" to clear it. A natural key matching nothing
		is a clear error (nothing written). Internally this still reads the current rules
		document and writes the whole thing back (rules storage is one document) — but the
		caller never sees or supplies its version; a version race is retried a bounded
		number of times. Requires tasks:write — and deliberately NOT methodology:write: prose
		can never change a rule (it cannot touch structure, and the guide derives every
		invariant from structure, rendering prose only as an additive note), so documenting
		the process stays a routine write.
		""")]
	public static async Task<MethodologyDescribeResult> MethodologyDescribeAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Instance name (slug) whose rules the primitive lives on.")] string name,
		[Description("kind | status | transition | effect | constraint | linkKind | tagAxis.")] string primitive,
		[Description("The new prose. Pass \"\" to clear an existing description.")] string description,
		[Description("Kind slug — required for every primitive except linkKind/tagAxis.")] string? kind = null,
		[Description("Any one type slug of the owning workflow block — required for status/transition/constraint.")] string? type = null,
		[Description("Status slug — required for primitive 'status'. Also doubles as the linkKind slug when primitive is 'linkKind'.")] string? slug = null,
		[Description("Transition source status — required for primitive 'transition'.")] string? from = null,
		[Description("Transition target status — required for primitive 'transition'.")] string? to = null,
		[Description("Effect trigger status — required for primitive 'effect'.")] string? on = null,
		[Description("Effect relation kind — required for primitive 'effect'; also the constraint's link for primitive 'constraint'.")] string? link = null,
		[Description("Effect direction (incoming|outgoing) — required for primitive 'effect'.")] string? direction = null,
		[Description("Effect onLeave flag — matches Effect.onLeave; default false (entering).")] bool onLeave = false,
		[Description("Tag axis namespace — required for primitive 'tagAxis'.")] string? @namespace = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		// DELIBERATELY NOT methodology:write, though it writes a live instance's rules document
		// through the same DefineMethodologyInstanceRulesAsync as the gated rules_upsert. The
		// line is not "which service call" but "can it change the process":
		//
		// 1. MethodologyDescribe.Apply only ever does `with { Description = ... }` on a record
		//    found by natural key; every branch maps over the lists preserving shape. It cannot
		//    add, remove or reorder a kind, block, status, transition, effect or constraint.
		// 2. MethodologyGuide derives EVERY invariant from structural fields — not one
		//    invariants.Add reads .Description. Prose is strictly additive decoration: a
		//    transition renders "OWNER-ONLY (enforced)" from RequiresApproval/EnforceApproval
		//    and the prose lands beside it as "note: ...". A lying description cannot delete
		//    the mark it sits next to, and the machine-readable invariants never see it at all.
		//
		// So the worst this verb can do is write a misleading comment next to a rule that stays
		// visibly in force — the same exposure any task body already has under tasks:write.
		// Gating it would put routine process documentation behind the governance scope and rot
		// the docs. Locked by Methodology_Describe_NeedsNoMethodologyWrite.
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		const int maxAttempts = 5;
		for (var attempt = 1; ; attempt++)
		{
			var view = await tasks.GetMethodologyInstanceRulesAsync(projectKey, name, ct)
				?? throw new ArgumentException($"methodology instance '{name}' not found in project '{projectKey}'");
			var (def, matched) = MethodologyDescribe.Apply(
				view.Definition, primitive, kind, type, slug, from, to, on, link, direction, onLeave, @namespace, description);
			if (!matched)
				throw new ArgumentException($"no {primitive} matched the given natural key on methodology instance '{name}'");
			try
			{
				var ack = await tasks.DefineMethodologyInstanceRulesAsync(projectKey, name, def, view.Version, null, ct);
				return new MethodologyDescribeResult(ack.Name, primitive, ack.Version);
			}
			catch (InvalidOperationException ex) when (attempt < maxAttempts && ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase))
			{
				// Someone else wrote the rules document between our read and our write — the
				// prose target itself didn't change, only the version cursor did. Re-read and
				// reapply rather than surfacing a CAS conflict for a call that never asked the
				// caller to track a version.
			}
		}
	}

	// ---- named methodology templates (inert process documents; builtins are templates) ----

	[McpServerTool(Name = "tasks_methodology_template_upsert", Title = "Upsert a named methodology template", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyTemplateUpsertResult))]
	[Description("""
		Store a NAMED METHODOLOGY TEMPLATE — a reusable process document (kinds/types/
		statuses/transitions) independent of running methodology instances. Does NOT create
		boards and does NOT rewrite live nodes (no migration planner — templates are inert
		documents). `key` is the template slug; `version` is the watermark baseline from your
		last template_get (0 = create). Builtin keys (quartet|classic|simple) are read-only
		and rejected on write — copy into a new key instead. `definition` shape:
		{ name, kinds:[{ kind, quickAddAllowed?, workflows:[{ types, statuses, transitions }],
		linkConstraints?, effects? }], linkKinds?, tagAxes? }. To make a template LIVE, call
		tasks_methodology_create(source="template", sourceKey=<key>). Returns
		{ key, version, changed }. Requires tasks:write.
		""")]
	public static async Task<MethodologyTemplateUpsertResult> MethodologyTemplateUpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Template slug key (^[a-z][a-z0-9_-]{0,99}$; not a builtin name).")] string key,
		[Description("The methodology template document (kinds/workflows/linkKinds/tagAxes).")] MethodologyDefInput definition,
		[Description("Watermark baseline: version from last template_get; 0 = create.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var def = MethodologyWire.ParseDefinition(definition);
		var ack = await tasks.UpsertMethodologyTemplateAsync(projectKey, key, def, version, ct);
		return new MethodologyTemplateUpsertResult(ack.Key, ack.Version, ack.Changed);
	}

	[McpServerTool(Name = "tasks_methodology_template_delete", Title = "Delete a named methodology template", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyTemplateDeleteResult))]
	[Description("""
		Delete a STORED named methodology template (temporal soft-close). Builtin keys
		(quartet|classic|simple) are read-only and rejected. Does NOT touch methodology
		instances, boards, or nodes. Missing key is an idempotent no-op (deleted:false).
		`version` is the watermark baseline from template_get. Requires tasks:write.
		""")]
	public static async Task<MethodologyTemplateDeleteResult> MethodologyTemplateDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Template slug key to delete.")] string key,
		[Description("Watermark baseline from last template_get; 0 = delete current regardless.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var ack = await tasks.DeleteMethodologyTemplateAsync(projectKey, key, version, ct);
		return new MethodologyTemplateDeleteResult(ack.Key, Deleted: ack.Changed, ack.Version);
	}

	[McpServerTool(Name = "tasks_methodology_template_get", Title = "Get a named methodology template", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyTemplateGetResult))]
	[Description("""
		Return ONE methodology template by `key`. Resolution order: stored template →
		builtin (quartet|classic|simple, source="builtin", version 0) → dual-read of the
		legacy project singleton under key "methodology" (source="definition", compat) →
		found:false. Document body (name/kinds/…) is copyable into template_upsert or
		into tasks_methodology_rules_upsert for a live instance. Requires tasks:read.
		""")]
	public static async Task<MethodologyTemplateGetResult> MethodologyTemplateGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Template slug key (stored, builtin quartet|classic|simple, or methodology for dual-read of the legacy singleton).")] string key,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var view = await tasks.GetMethodologyTemplateAsync(projectKey, key, ct);
		if (view is null)
			return new MethodologyTemplateGetResult(Found: false, Key: key);
		return MethodologyWire.ProjectTemplate(view.Key, view.Source, view.Definition, view.Version, view.Created, view.Updated);
	}

	[McpServerTool(Name = "tasks_methodology_template_list", Title = "List methodology templates", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyTemplateListResult))]
	[Description("""
		List methodology templates available to the project: always the builtins
		(quartet|classic|simple, source="builtin"), then any stored templates
		(source="stored"), plus a dual-read entry for the legacy singleton under key
		"methodology" (source="definition") when present and not shadowed by a stored
		template of that key. Compact rows (key/source/name/version/updated) — use
		template_get for the full document. Requires tasks:read.
		""")]
	public static async Task<MethodologyTemplateListResult> MethodologyTemplateListAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var items = await tasks.ListMethodologyTemplatesAsync(projectKey, ct);
		return new MethodologyTemplateListResult(
			items.Select(i => new MethodologyTemplateListItemView(i.Key, i.Source, i.Name, i.Version, i.Updated)).ToList());
	}

	[McpServerTool(Name = "tasks_methodology_template_snapshot", Title = "Snapshot rules into a named methodology template", UseStructuredContent = true, OutputSchemaType = typeof(MethodologyTemplateUpsertResult))]
	[Description("""
		Snapshot process rules into a NAMED TEMPLATE without mutating the source. `from`
		defaults to "effective" = the legacy project singleton if present, else the builtin
		quartet document. Explicit sources: "preset:quartet|classic|simple" (or the bare
		builtin slug); "instance:<key>" = the named methodology instance's rules. Write is
		template-only — no boards created, no live nodes rewritten. `key`/`version` same
		watermark posture as template_upsert. Requires tasks:write.
		""")]
	public static async Task<MethodologyTemplateUpsertResult> MethodologyTemplateSnapshotAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Destination template slug key (not a builtin name).")] string key,
		[Description("Watermark baseline for the destination template; 0 = create.")] long version = 0,
		[Description("Source: effective (default), preset:quartet|classic|simple, or instance:<key>.")] string? from = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var ack = await tasks.SnapshotMethodologyTemplateAsync(projectKey, key, version, from, ct);
		return new MethodologyTemplateUpsertResult(ack.Key, ack.Version, ack.Changed);
	}

	[McpServerTool(Name = "tasks_methodology_guide", Title = "How to work this project's process (runtime-derived guide)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MethodologyGuideView))]
	[Description("""
		Return the AGENT ONBOARDING GUIDE for this project's process — how to work its
		boards — DERIVED AT RUNTIME from OPEN methodology INSTANCE rules (tasks_methodology_create
		/ tasks_methodology_rules_upsert), with builtin templates (quartet|classic|simple)
		as the baseline where no open instance applies. Optional `name` selects one instance
		explicitly. When omitted, resolution follows the project's ACTIVE INSTANCE pointer
		(spec methodology-active-instance, tasks_methodology_active_get /
		tasks_methodology_set_active): the pointer when set and open; else the single open
		instance when there is exactly one; else an EXPLICIT "N open, none active" guide
		naming every open instance — never a silent merge of their kinds. Call it when you
		start working a project's tasks and need the process rules. `markdown` covers, per
		effective kind: types (quick-add default marked), statuses grouped open/terminal,
		initial status, the transition map (collapsed to "free" when a block allows every
		move), the GATES as behavioral invariants (owner-only transitions the agent NEVER
		performs — marked enforced vs convention, reason-required moves, artifact:<slug>
		comment preconditions, pre-transition checklists), creation link requirements
		(specRef/blockedBy/ideaRef, incl. declared link targets), declared transition
		effects, tag axes (or free-form), and the relation-kind dictionary (process vs
		neutral vs instance-declared). `invariants` is the same derivation machine-readable:
		[{ kind, rule: approval_gate|approval_gate_enforced|reason_required|
		reason_required_convention|precondition_artifact|precondition_artifact_convention|
		checklist|transition_effect|link_constraint|tag_axes, detail }]. The "_convention"
		variants (schema v2, spec methodology-gate-strictness) mean the gate is declared but
		NOT server-blocked (Enforce.Artifacts:false on the transition) — every builtin preset
		and every already-stored definition keeps the enforced variant, unchanged.
		`source` = instance|active|ambiguous|presets; `definitionVersion` when a single
		instance is selected (named, active, or the unambiguous single open one). Bounded (a
		handful of kinds) — no truncation. Requires tasks:read.
		""")]
	public static async Task<MethodologyGuideView> MethodologyGuideAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey,
		[Description("Optional methodology instance name; when omitted, resolves via the active-instance pointer (tasks_methodology_active_get/set_active).")] string? name = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		return await tasks.GetMethodologyGuideAsync(projectKey, name, ct);
	}

	// The definition wire mapping (ParseDefinition/ParseMigration/ProjectDefinition) lives in
	// MethodologyWire — shared with the admin methodology-editor page, so the editor's JSON is
	// shape-identical to the template/rules documents.

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
		[LogArg][Description("Body length knob (uniform contract): omitted = the FULL body (this is the pointed full read); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("Include an absolute `url` permalink to the node's detail page (off by default).")] bool includeUrl = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		var detail = await tasks.GetNodeOnBoardAsync(projectKey, board, node, urlPrefix, ct);
		// Uniform bodyLen contract, default FULL (the pointed read); shape the wire body only.
		return detail with { Node = detail.Node with { Body = ModuleMcp.Body(detail.Node.Body, bodyLen, ModuleMcp.FullBody) ?? "" } };
	}

	[McpServerTool(Name = "tasks_search", Title = "Read plan nodes (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(TaskSearchResultView))]
	[Description("""
		THE read verb for plan nodes — one tool for LISTING (no `q`) and hybrid SEARCH (`q`).
		Nodes are FLAT (a single slug `key`); hierarchy is the part_of edge (parentSlug/`depth`).
		Bodies follow the uniform `bodyLen` knob (omitted = a ~240-char snippet, -1 = full, or
		tasks_node_get); a row's `version` is the CAS baseline for a later upsert. Hard ~30k-char
		output budget — overflow rows are prefix-cut + flagged. `statusKind` visibility defaults
		when omitted (open+terminalok for a query, open for a listing) — the response echoes the
		applied set as `effectiveStatusKind`, so the default is never silent. Requires tasks:read.

		Cost — your context pays it. Same query, same rows: bodyLen:0 = 1x, default snippet
		~1.5-2x, bodyLen:-1 ~3x+ and unbounded per row — a single long node body can add
		thousands of chars on its own.
		Cheap path: search with bodyLen:0, read the titles, then tasks_node_get the 1-3 keys
		you actually need. Use -1 only when you already know the keys and there are few.
		Pulling full bodies across a wide limit "just in case" is the most expensive habit
		available here: it routinely spends a third of the response budget on text you will not read.
		[[full]]
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
		omit `board` for a project-wide list. Default order: priority then key. A default
		listing shows only `open` nodes (terminal part_of ancestors of a visible node are kept
		so the tree stays connected); widen with the `statusKind` facet. With `q`: a RELEVANCE
		selection via hybrid search over name/body/tags (lexical FTS5 ⊕ semantic vectors,
		RRF-fused; semantic is silently absent when no embedding is configured); the fused
		ranking supplies a bounded candidate pool of max(3×limit, 50). A default query already
		reaches terminal-OK nodes (accepted on ideas, Done on work — a SUCCESS state, not
		"closed"; search-before-rework needs to find these). Default order: relevance; the
		response carries `retrievers` {lexical, semantic, degraded}.

		VISIBILITY (`statusKind`, both modes). Status visibility is a facet, NOT a boolean
		"closed": `statusKind` is a SET over open | terminalok | terminalcancel (open = not
		finished; terminalok = accepted/Done; terminalcancel = rejected/cancelled). Omitting it
		is NEUTRAL — a default query returns open+terminalok (so accepted/Done are always
		findable), a default listing returns open. `includeClosed` is a DEPRECATED alias:
		true → the facet is omitted (every kind); false + q → [open, terminalok]; false without
		q → [open]. An explicit `statusKind` OVERRIDES `includeClosed`. The response echoes
		`effectiveStatusKind`: the facet that ACTUALLY applied, including when defaulted — a
		default query surfaces `["open","terminalok"]`, a default listing `["open"]`, an explicit
		`statusKind` is echoed back resolved (normalized/deduped), and `includeClosed:true` with
		no explicit `statusKind` echoes `null` (NEUTRAL — no facet applied, every kind). Defaulted
		visibility is never silent.

		FILTERS (predicates in BOTH modes, all SOFT — an unresolved filter value scopes to an
		empty result, never an error): `under` = a part_of subtree root (slug or NodeId; a slug
		resolves on `board`, or project-wide when board is omitted; a root that matches nothing →
		an empty result, an ambiguous slug → the union of its subtrees); `status` = keep only
		these slugs (case-insensitive; naming a TERMINAL status returns its nodes even without
		widening the statusKind facet — an explicit ask; an unknown slug is silently dropped, and
		an all-unknown set → an empty result); `keys` = a SOFT node filter (slug|NodeId mixed) — a ref that matches nothing
		is silently dropped (NOT an error), an ambiguous cross-board slug contributes ALL its
		matches, terminal nodes are included, and an all-missing keys set yields an empty result;
		`commit` = keep only nodes carrying that commit SHA (exact, or a >=7-hex prefix resolving a stored full sha).

		SORT: `sort` = {by: priority|created|updated|title|relevance, desc?}. Without `q`
		the default is priority (asking for relevance is an error); with `q` the default is
		relevance, and an explicit sort reorders WITHIN the relevance-selected set (`desc`
		is ignored for relevance). `limit` caps the rows (with `q` it defaults to 20, 0 =
		no cap; a listing is unbounded by default — the output budget still applies).

		With `q` each row carries `score` (the fused, rank-based relevance) and `retriever`
		("lexical" = lexically confirmed, "semantic" = surfaced by the vector leg alone,
		"exact" = an exact slug/NodeId match — tried literally AND as a kebab-normalized
		candidate, so "methodology redesign" also reaches the `methodology-redesign` slug;
		an exact hit is SUBJECT TO the statusKind facet like any other — it leads the ranking
		but does not override visibility, so an exact match on a terminal-CANCEL node needs
		statusKind:[terminalcancel] (terminal-OK is found by default)); a semantic-only hit
		below the relevance floor is
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
		[LogArg(LogArgMode.Presence)][Description("Search query. Omit for a deterministic listing (list = search without q).")] string? q = null,
		[Description("Scope to one board (listing then carries kind/specBoard/currentVersion). Omit = the whole project; each row names its board.")] string? board = null,
		[Description("Restrict to the part_of subtree under this node (slug or 32-hex NodeId). A root that matches nothing scopes to an empty result (not an error); an ambiguous slug uses the union of its subtrees.")] string? under = null,
		[Description("Keep only these status slugs (case-insensitive). A terminal status listed here is returned even when includeClosed=false. An unknown slug is silently dropped; an all-unknown set yields an empty result (not an error).")] string[]? status = null,
		[Description("Soft node filter: slugs and/or 32-hex NodeIds, mixed. A ref that matches nothing is silently dropped (never an error), an ambiguous cross-board slug contributes all its matches, terminal nodes included; an all-missing set yields an empty result.")] string[]? keys = null,
		[LogArg][Description("DEPRECATED alias for statusKind — prefer statusKind. Maps onto the facet: includeClosed:true → omit the facet (every kind); includeClosed:false + q → statusKind:[open,terminalok]; includeClosed:false without q → statusKind:[open]. Ignored when statusKind is set. (A default query already reaches terminal-OK — accepted/Done — regardless of this flag.)")] bool includeClosed = false,
		[Description("Sort order: {by: priority|created|updated|title|relevance, desc?}. Default: priority (listing) / relevance (with q).")] SortInput? sort = null,
		[Description("Tag PROJECTION instead of rows: an ordered, comma-separated list of tag namespaces (e.g. \"area,concern\"). Needs board; not with q.")] string? groupBy = null,
		[LogArg][Description("Body length knob (uniform contract): omitted = a ~240-char snippet (the compact listing default — fetch a full body with tasks_node_get or bodyLen:-1); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[LogArg][Description("Max rows returned. Default: unbounded listing / 20 with q (0 = no cap).")] int? limit = null,
		[Description("Include an absolute `url` permalink to each node's detail page (off by default).")] bool includeUrl = false,
		[Description("Reverse commit lookup: keep only nodes carrying this commit SHA — an exact match, or a >=7-hex prefix that resolves a stored full sha. Applies in both modes.")] string? commit = null,
		[Description("Visibility facet: keep only nodes whose statusKind is in this SET — values open | terminalok | terminalcancel (open = not finished; terminalok = accepted/Done, a SUCCESS state; terminalcancel = rejected/cancelled). Applies in BOTH modes against the same authority. Omit = NEUTRAL (every kind; a default read still finds accepted/Done). This is the first-class replacement for the deprecated includeClosed and OVERRIDES it when set; an unknown value is an error.")] string[]? statusKind = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
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
			Filter = new TaskNodeFilter(board, under, status, keys, includeClosed, commit, statusKind),
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
			Retrievers: res.Retrievers is { } r ? new RetrieverInfo(r.Lexical, r.Semantic, r.Degraded, r.DegradedReason, r.SemanticLag, r.Reranked) : null,
			Truncated: omitted > 0 ? true : null,
			Omitted: omitted > 0 ? omitted : null,
			Hint: omitted > 0 ? SearchBudgetHint : null,
			EffectiveStatusKind: res.EffectiveStatusKind);
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
		Declarative temporal PATCH-upsert of plan nodes (omitted field = unchanged; tags:[] clears;
		delete via {key, deleted:true}). Each node has a FLAT slug `key` and nests via `partOf`.
		`body` is GFM markdown — `##` headings and REAL newlines, NOT literal `\n`, NOT `==headings==`.
		`version` is a WATERMARK baseline (board `currentVersion` OR the node's own version; 0 = new);
		`applied` is the SINGLE source of truth — false = nothing written, see conflicts[]. tasks:write.
		Cyrillic bodies: send raw UTF-8, not \uXXXX escapes (triples the byte size) — an oversized
		call is silently truncated before the server sees it. Split large batches.
		[[full]]
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
		`autoResolved[]`). Rename via prevKey. The `board` must ALREADY exist — a cold
			tasks_upsert to an unknown board is REJECTED (with a "did you mean 'X'?" suggestion), not
			auto-created; create it first with tasks_board_create, or provision a methodology.

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
		string projectKey, [LogArg] string board,
		[Description("Array of node objects: flat `key`, optional `partOf` (parent slug|NodeId), `tags` (array of ns:value), `commits` (array of hex SHAs), `specRef` (spec slug|NodeId), `ideaRef`, `blockedBy` (blocker slug|NodeId), `supersedes`, status/type/title/body/reason (for RequiresReason transitions — never the body)/priority/version, and `prevKey` to rename.")] PlanNodeInput[] nodes,
		[Description("Body length knob (uniform contract): omitted = NO body (the compact ack default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("Include an absolute `url` permalink to each returned node's detail page (off by default).")] bool includeUrl = false,
		[Description("Batch policy. TRUE (default) = ATOMIC: any conflict/refusal aborts the WHOLE call, nothing is written. FALSE = PARTIAL apply (explicit opt-in): valid nodes LAND, each refused node comes back in conflicts[] with its own reason (a stale baseline is one such per-node refusal, not a failed call), and a node referencing a refused node of the SAME call (partOf/blockedBy/supersedes, transitively) is refused too — so a partial write never leaves a dangling reference. added/updated/removed then echo exactly the nodes that landed.")] bool atomic = true,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		await AssertBoardKnownAsync(tasks, projectKey, board, ct);
		// The SESSION key's scopes decide the actor capability: tasks:approve elevates the
		// write past methodology-ENFORCED approval gates (enforceApproval transitions).
		var actor = ModuleMcp.HasScope(http, ApiKeyScopes.TasksApprove) ? TasksActor.Approver : TasksActor.None;
		var patches = ParseNodePatches(nodes);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.UpsertAsync(projectKey, board, patches, actor, atomic, ct), urlPrefix, bodyLen);
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
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var urlPrefix = await UrlPrefixAsync(http, tasks, projectKey, includeUrl, ct);
		return Serialize(await tasks.DeltaAsync(projectKey, board, sinceVersion, ct), urlPrefix, bodyLen);
	}

	[McpServerTool(Name = "tasks_workflow", Title = "Board workflow (kinds/statuses/transitions)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(WorkflowView))]
	[Description("Return the workflow for a board: its kind plus `workflows` — one block per DISTINCT state machine, each carrying `types` (every type slug sharing that FSM; e.g. feature|bug|chore on a work board are one block), the initial status, statuses (slug, name, kind=open|terminalok|terminalcancel) and transitions (from, to, requiresApproval, requiresReason, enforceApproval [true = the server BLOCKS the transition unless the actor can approve; false = owner-only by convention], preconditionArtifact? — a comment-artifact tag the node must carry before the transition). A kind a methodology instance's rules declare resolves from those rules; other kinds report the built-in template. Use this to learn the legal types/statuses before tasks_upsert. Requires tasks:read.")]
	public static async Task<WorkflowView> WorkflowAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
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
				Transitions: g.Workflow.Transitions.Select(t => new WorkflowTransitionView(t.From, t.To, t.RequiresApproval, t.RequiresReason, t.EnforceApproval, t.PreconditionArtifact)).ToList())).ToList());
	}

	// ---- adapter plumbing: JSON parsing + wire shaping (no domain logic) ----

	// Build the absolute permalink prefix for this project's nodes
	// ("{scheme}://{host}/ui/{ws}/{project}/tasks/node/"), or null when include_url is off or
	// the workspace can't be resolved. Per-node url = prefix + "{board}/{slug}" (the canonical
	// slug-URL, node-slug-addressable); the prefix ends with "/tasks/". scheme/host come from
	// the request (honor forwarded headers behind a proxy).
	// The namespace-creation GATE for task boards (spec agent-namespace-provisioning, variant C —
	// hard opt-in): a tasks_upsert naming a board that does not exist is REJECTED with a
	// did-you-mean, not auto-created. Boards are created explicitly (tasks_board_create) or
	// provisioned by a methodology (tasks_methodology_create → CreateBoardAsync) — both land the
	// board BEFORE it is ever typed here, so the only thing this rejects is a typo/hallucinated
	// name. No reserved-name allowlist: unlike memory stores there is no board that comes into
	// being merely by typing it through this verb. The gate lives at the MCP tool layer ONLY — the
	// service door (TasksService.UpsertAsync) still auto-vivifies for its internal callers
	// (report_issue's triage board, etc.).
	static async Task AssertBoardKnownAsync(ITasksService tasks, string projectKey, string board, CancellationToken ct)
	{
		var name = board?.Trim() ?? "";
		if (await tasks.BoardExistsAsync(projectKey, name, ct)) return;

		var existing = (await tasks.ListBoardsAsync(projectKey, ct)).Select(b => b.Name);
		var near = NamespaceSuggest.Nearest(name, existing);
		var hint = near.Count == 0 ? "" : $" Did you mean {string.Join(" / ", near.Select(n => $"'{n}'"))}?";
		throw new InvalidOperationException(
			$"Task board '{name}' does not exist in '{projectKey}'.{hint} "
			+ "Create it explicitly with tasks_board_create (or provision a methodology with "
			+ "tasks_methodology_create) — tasks_upsert no longer auto-creates a board (a cold write "
			+ "used to silently create a simple board that then lived forever).");
	}

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
				Reason = n.Reason,
				// Commits: null = omit (don't touch); a non-null list (incl. empty) REPLACES the
				// node's full commit set — same semantics as Tags.
				Commits = n.Commits,
				Priority = n.Priority,
				// links:{kind:ref|ref[]} → kind -> normalized string list (the converter already
				// flattened a bare ref to a one-element list). Empty-value kinds are dropped.
				Links = n.Links is null ? null : n.Links
					.Where(kv => kv.Value.Values.Count > 0)
					.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Values, StringComparer.Ordinal),
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
