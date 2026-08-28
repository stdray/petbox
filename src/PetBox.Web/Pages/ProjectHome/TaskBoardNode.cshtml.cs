using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.ProjectHome;

// Detail for ONE node of a task board, addressed by its stable NodeId alone (board-independent):
// /ui/{ws}/{project}/tasks/node/{nodeId}. Shows the full body (no truncation) + the discussion
// thread — the destination the board's abbreviated rows link to — and lets a human EDIT the node
// (status, title, body) in place. Sibling of TaskBoard; named TaskBoardNode (not TaskNode) so it
// doesn't shadow the PetBox.Tasks.Data.TaskNode record. Every read AND write goes through
// ITasksService / ICommentService — the page never opens the DB context itself, and the edit
// handlers route through UpsertAsync so a UI edit hits the SAME guards (FSM, link constraints, concurrency)
// as the MCP path (spec edit-respects-guards). NetArchTest enforces the door.
// viewer-member-consistency: the class policy is WorkspaceViewer (a Viewer must be able to READ
// the node) — every OnPost* handler (status change, title/body edit, comments) is a MUTATION and
// guards itself to Member+.
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class TaskBoardNodeModel : PageModel
{
	// editor-preview-renders-server-side: the largest draft OnPostPreviewAsync will render in one
	// request. Public so the test that pins the refusal reads the SAME number the handler enforces
	// rather than restating it.
	public const int MaxPreviewChars = 262_144;

	// The preview fragment is already-sanitized HTML written verbatim into the response; the
	// charset is explicit so a body of Cyrillic prose round-trips as UTF-8 rather than inheriting a
	// guess (the renderer emits raw non-ASCII, it does not entity-escape it).
	const string PreviewContentType = "text/html; charset=utf-8";

	readonly FeatureFlags _features;
	readonly ITasksService _tasks;
	readonly ICommentService _comments;
	readonly ISettingsResolver _settings;
	// Optional ctor param (see TaskBoardModel): DI always supplies it; a page-model unit test that
	// doesn't exercise memory autolinking may omit it. Null = keys render literal.
	readonly PetBox.Memory.Contract.IMemoryService? _memory;
	// Optional ctor param, same posture as _memory above: needed only to gate MemoryRefMap's derived
	// workspace-container leg (SandboxContainment.PermitsAsync) — DI always supplies it (IProjectCatalog
	// is registered unconditionally), a bare unit-test construction that never exercises memory
	// autolinking may omit it.
	readonly IProjectCatalog? _catalog;
	// editor-preview-renders-server-side: the ONE markdown pipeline, now serving the live edit
	// preview too (OnPostPreviewAsync) and not only the saved read surfaces. Optional ctor param
	// with the same posture as _memory/_catalog above — DI always supplies it (registered singleton
	// in Program.cs), and a bare page-model unit test that never previews may omit it. The fallback
	// is a fresh MarkdownRenderer rather than a null-render: the renderer is stateless and
	// configuration-identical per instance, so the preview's output cannot drift from the saved
	// body's just because a test constructed the page by hand.
	readonly IMarkdownRenderer _markdown;

	public TaskBoardNodeModel(FeatureFlags features, ITasksService tasks, ICommentService comments,
		ISettingsResolver settings, PetBox.Memory.Contract.IMemoryService? memory = null,
		IProjectCatalog? catalog = null, IMarkdownRenderer? markdown = null)
	{
		_features = features;
		_tasks = tasks;
		_comments = comments;
		_settings = settings;
		_memory = memory;
		_catalog = catalog;
		_markdown = markdown ?? new MarkdownRenderer();
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	// The page is reachable two ways: the canonical human-readable slug-URL
	// /tasks/{board}/{slug} (board+slug bound) and the stable opaque alias /tasks/node/{nodeId}
	// (nodeId bound). Exactly one set is present per request; ResolveAsync picks the resolver.
	[BindProperty(SupportsGet = true, Name = "nodeId")]
	public string NodeId { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "board")]
	public string Board { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "slug")]
	public string Slug { get; set; } = string.Empty;

	// The resolved node (board + enriched view + breadcrumb ancestors). Set on a 200.
	public NodeDetailView Detail { get; private set; } = default!;
	// live-verification finding (owner screenshot, 2026-08-28): the regression banner's
	// "fixed by" link used to always show the raw NodeId — PetBox's UI convention prefers a slug
	// when one resolves. Observation.FixedByNodeId is a plain stored NodeId (not a graph edge), so
	// it needs its OWN resolution, reusing GetNodeAsync — the same door the exhaustive relations
	// panel below already resolves every OTHER link target through (ObservationFixedByResolver).
	// Null when this node has no Observation, or no FixedByNodeId, or the resolver isn't reached.
	public LinkDto? ObservationFixedByLink { get; private set; }
	// The node's discussion thread, DFS-flattened (shared shape with the board page).
	public IReadOnlyList<CommentLine> Thread { get; private set; } = [];
	// Legal next statuses from the node's current status (edit-status). The status form offers
	// only these; an empty list means no transition is available (e.g. a terminal status).
	public IReadOnlyList<string> NextStatuses { get; private set; } = [];
	// The same legal next statuses, each carrying the approval gate DECLARED on the transition that
	// reaches it (approval-gate-enforced-visible). The select used to render a gated target exactly
	// like an ungated one — classic's `Done` (owner-only, unenforced) looked like `Todo` — so the one
	// question a gate exists to answer ("is this mine alone, or can an agent push it too?") was
	// invisible at the only place the move is made. Marking only: the option stays selectable, the
	// owner is an approving actor either way (ApplyAsync passes TasksActor.Approver).
	public IReadOnlyList<NextStatusOption> NextStatusOptions { get; private set; } = [];
	// Set when a write was rejected (a guard violation or an optimistic-concurrency conflict):
	// the edit is re-rendered with the message inline rather than silently dropped.
	public string? Error { get; private set; }

	// The node board's workflow surface, embedded as a JSON island for the "View workflow" modal
	// (ts/workflow-viz.ts). The node-type badge opens the block matching the node's type. Resolved
	// through MethodologyRuntime, so user-defined methodologies visualize out of the box.
	public string? WorkflowJson { get; private set; }

	// The project's FSM resolution seam (methodology definition merged over the presets) — the
	// SAME surface MCP resolves through. The view resolves the node's kind name, badge colour
	// and terminality off this + Detail.Kind (the board's resolved KindName, a valid runtime
	// input), so a definition-declared custom kind renders as it behaves, not as `Simple`.
	public MethodologyRuntime Runtime { get; private set; } = MethodologyRuntime.PresetsOnly;

	// The project's commit-view URL template (RepoSettings, Scope.Project). When set, the commit-ref
	// chip links to it and commit hashes in the body / comments autolink. Empty = plain text.
	public string? CommitUrlTemplate { get; private set; }

	// Resolved `[[slug]]` mentions found in the body + comment bodies (node-ref-autolink), keyed by
	// the MENTIONED slug. Handed to the markdown renderer so those mentions become links. Empty when
	// nothing resolved.
	public IReadOnlyDictionary<string, NodeRefTarget> NodeRefs { get; private set; }
		= new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);

	// Resolved memory-entry keys (`m-<32hex>` / `ac-<12hex>`) mentioned in the body or any comment
	// (memory-key-mention-link) — one batch per container, unresolved/ambiguous keys absent (they
	// render literal). Empty when the Memory feature is off.
	public IReadOnlyDictionary<string, NodeRefTarget> MemoryRefs { get; private set; }
		= new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await LoadAsync(ct);
	}

	// edit-status: move the node to a new status. The select offers only NextStatuses, but the
	// service re-validates the transition (and runs the FSM effects) — the UI can't bypass it.
	public async Task<IActionResult> OnPostStatusAsync(string status, long version, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await ApplyAsync(p => p with { Status = status }, version, ct);
	}

	// edit-title-body: replace the node's title and body (markdown). An empty field clears it.
	public async Task<IActionResult> OnPostEditAsync(string? title, string? body, long version, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await ApplyAsync(p => p with { Title = title ?? string.Empty, Body = body ?? string.Empty }, version, ct);
	}

	// editor-preview-renders-server-side: render the DRAFT body through the same IMarkdownRenderer
	// that renders the saved body, and return the sanitized HTML fragment htmx swaps into the
	// preview pane. The old preview was a SECOND markdown pipeline (ts/markdown.ts: marked +
	// DOMPurify) whose output diverged structurally from this one — `##` never became a
	// `<section class="md-section">` and `> [!NOTE]` rendered as a plain blockquote containing the
	// literal text "[!NOTE]". Owner's call (2026-08-28): delete that pipeline rather than chase
	// parity between two implementations forever; the network round-trip per recompute is the
	// consciously accepted price.
	//
	// The parameter is TEXT, not a node reference, because the draft does not exist in the database
	// yet — which makes this an authenticated endpoint that renders arbitrary caller-supplied
	// markup, so it is fenced on four sides:
	//   * Member+ (NOT the class-level WorkspaceViewer): only someone who could actually SAVE this
	//     body may render one. Same check, same order, as OnPostEditAsync above.
	//   * the Tasks feature flag, like every other handler here;
	//   * the node the editor sits on must still RESOLVE for this caller (ResolveAsync honours the
	//     route's tenant binding) — so this is a node-scoped preview, not a general-purpose
	//     "render markdown for me" oracle reachable by any workspace member;
	//   * MaxPreviewChars, below.
	// XSS is handled by construction rather than by a second sanitizer: the response is whatever
	// MarkdownRenderer.RenderToHtml returns, which is already through the SAME BuildSanitizer() and
	// the SAME AllowedClasses as the saved body — author raw HTML, `on*` handlers and
	// `javascript:` URLs are neutralized in the preview exactly as they are after saving.
	public async Task<IActionResult> OnPostPreviewAsync(string? body, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		var detail = await ResolveAsync(ct);
		if (detail is null) return NotFound();

		var draft = body ?? string.Empty;
		// A ceiling on ONE preview render, not on what may be saved: bodies are unbounded in
		// storage, so a body past this point still saves fine and simply previews as this notice
		// instead of a render. Deliberately far above any hand-written node body (the largest on
		// the real boards are a few KB) so a human never meets it, and far below the input size at
		// which a debounced-per-keystroke Markdig parse becomes a cheap way to burn the box's CPU.
		if (draft.Length > MaxPreviewChars)
			return Content(
				$"<p class=\"opacity-60 italic\">Body is too large to preview ({draft.Length:N0} characters; " +
				$"the limit is {MaxPreviewChars:N0}). It can still be saved.</p>", PreviewContentType);

		// The SAME three context inputs the saved body is rendered with (see TaskBoardNode.cshtml's
		// read-body partial), pre-scanned over the DRAFT text rather than the stored one — that is
		// what makes `[[slug]]` mentions, commit hashes and memory keys resolve in the preview
		// instead of trading the old divergence for a new one. LoadAsync scans the body PLUS every
		// comment body, so its map can hold entries this one does not; that cannot move the output,
		// because the renderer only ever looks up mentions it actually finds in the text it is
		// rendering (see NodeRefs' own note that an unused map entry is harmless).
		var commitUrlTemplate = (await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey, ct)).CommitUrlTemplate;
		var nodeRefs = await NodeRefMap.BuildAsync(_tasks, WorkspaceKey, ProjectKey, [draft], ct);
		var memoryRefs = _memory is not null && _features.IsEnabled(Feature.Memory)
			? await MemoryRefMap.BuildAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, [draft], ct)
			: MemoryRefs;

		return Content(_markdown.RenderToHtml(draft, commitUrlTemplate, nodeRefs, memoryRefs), PreviewContentType);
	}

	// decision-pending-has-no-ui: set or clear the owner-decision-pending flag from the UI — the
	// ONLY write door for it before this handler existed was tasks_upsert via an agent. Routes
	// through the SAME ApplyAsync (NodePatch → UpsertAsync) every other edit on this page uses, so
	// it hits the identical guards/concurrency check as the MCP path (edit-respects-guards); the
	// flag itself carries no FSM of its own (orthogonal to Status — owner-decision-pending-flag), so
	// there is nothing here for UpsertAsync to reject beyond the normal stale-version conflict.
	public async Task<IActionResult> OnPostDecisionPendingAsync(bool pending, long version, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await ApplyAsync(p => p with { DecisionPending = pending }, version, ct);
	}

	// comments-ui-edit: add a comment (or, when parentId is set, a reply) under this node. Goes
	// through ICommentService.AddAsync — the low-ceremony single-write door; the comments_upsert
	// MCP batch verb shares the SAME reply-parent / same-thread guards. `nodeId` rides along on the
	// form as a hidden field (shared markup with the board page) but is ignored here: the node
	// is always the one this page already resolves itself, never client-supplied.
	public async Task<IActionResult> OnPostCommentAddAsync(string? parentId, string body, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		var detail = await ResolveAsync(ct);
		if (detail is null) return NotFound();

		try
		{
			var author = User.Identity?.Name ?? "system";
			var result = await _comments.AddAsync(ProjectKey, detail.Board, detail.Node.NodeId, parentId, author, body, tags: null, ct);
			if (!result.Applied)
				return await LoadAsync(ct, "Could not add the comment — refresh and try again.");
		}
		catch (ArgumentException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return Redirect(Routes.TaskBoardNodeBySlug(WorkspaceKey, ProjectKey, detail.Board, detail.Node.Key));
	}

	// comments-ui-edit: edit a comment's body in place. `version` is the watermark baseline the
	// form rendered with; a stale one comes back as a conflict (Applied:false), surfaced as the
	// page Error rather than silently clobbering a comment that moved on since the render.
	public async Task<IActionResult> OnPostCommentEditAsync(string id, string body, long version, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		var detail = await ResolveAsync(ct);
		if (detail is null) return NotFound();

		try
		{
			var result = await _comments.EditAsync(ProjectKey, detail.Board, id, body, tags: null, version, ct);
			if (!result.Applied)
				return await LoadAsync(ct, "This comment changed since the page was opened — refresh and redo your edit.");
		}
		catch (ArgumentException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return Redirect(Routes.TaskBoardNodeBySlug(WorkspaceKey, ProjectKey, detail.Board, detail.Node.Key));
	}

	// comments-ui-edit: soft-delete a comment. Rejected (InvalidOperationException) while it
	// still has active replies — the guard from ICommentService.DeleteAsync, surfaced inline
	// instead of a raw 500.
	public async Task<IActionResult> OnPostCommentDeleteAsync(string id, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		var detail = await ResolveAsync(ct);
		if (detail is null) return NotFound();

		try
		{
			await _comments.DeleteAsync(ProjectKey, detail.Board, id, ct);
		}
		catch (InvalidOperationException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return Redirect(Routes.TaskBoardNodeBySlug(WorkspaceKey, ProjectKey, detail.Board, detail.Node.Key));
	}

	// Resolve the node by whichever address the request carried: the opaque nodeId alias, or
	// the canonical (board, slug). Either way the caller can't retarget the board/key.
	Task<NodeDetailView?> ResolveAsync(CancellationToken ct) =>
		NodeId.Length > 0
			? _tasks.GetNodeAsync(ProjectKey, NodeId, ct)
			: _tasks.GetNodeBySlugAsync(ProjectKey, Board, Slug, ct);

	// Resolve the node, apply the patch through the service door, and PRG on success. A guard
	// rejection (ArgumentException / InvalidOperationException) or an optimistic-concurrency
	// conflict re-renders the page with the error surfaced inline — the edit is never silently
	// bypassed (edit-respects-guards). On success redirect to the CANONICAL slug-URL regardless
	// of which form was used to reach the page (node-slug-addressable).
	async Task<IActionResult> ApplyAsync(Func<NodePatch, NodePatch> mutate, long version, CancellationToken ct)
	{
		var detail = await ResolveAsync(ct);
		if (detail is null) return NotFound();

		var patch = mutate(new NodePatch { Key = detail.Node.Key, Version = version });
		try
		{
			// The interactive UI is the cookie-authenticated owner — an APPROVING actor:
			// methodology-enforced approval gates (enforceApproval) are the owner's call.
			var outcome = await _tasks.UpsertAsync(ProjectKey, detail.Board, [patch], TasksActor.Approver, ct: ct);
			if (outcome.Result.Conflicts.Count > 0)
			{
				// A Stale conflict now names the moved fields (Reason) — surface them so the
				// user knows WHAT changed instead of just "re-open and retry".
				var reason = outcome.Result.Conflicts[0].Reason;
				return await LoadAsync(ct, string.IsNullOrEmpty(reason)
					? "This node changed since the page was opened — refresh and redo your edit."
					: $"This node changed since the page was opened ({reason}) — refresh and redo your edit.");
			}
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			return await LoadAsync(ct, ex.Message);
		}

		return Redirect(Routes.TaskBoardNodeBySlug(WorkspaceKey, ProjectKey, detail.Board, detail.Node.Key));
	}

	// Shared load for the GET and the error re-render: resolve the node, compute the legal next
	// statuses, and flatten its comment thread.
	async Task<IActionResult> LoadAsync(CancellationToken ct, string? error = null)
	{
		var detail = await ResolveAsync(ct);
		if (detail is null) return NotFound();
		Detail = detail;
		Error = error;

		// Resolve the legal next statuses through the SAME board-scoped runtime the MCP path
		// uses, keyed by the board's resolved kind — so an instance-declared kind offers
		// transitions from its OWN FSM instead of the `Simple` fallback ParseKind would collapse a custom slug to.
		Runtime = await _tasks.GetRuntimeForBoardAsync(ProjectKey, detail.Board, ct);

		// Project-scoped commit-view template (cascades to workspace/system); empty when unset.
		CommitUrlTemplate = (await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey, ct)).CommitUrlTemplate;

		var type = detail.Node.Type.Length == 0 ? null : detail.Node.Type;
		var workflow = Runtime.For(detail.Kind, type);
		NextStatuses = workflow?.NextFrom(detail.Node.Status) ?? [];
		// Same list, plus the gate declared on the edge that reaches each target (the UI marks it;
		// nothing here blocks — the service re-validates the move on POST either way).
		NextStatusOptions = [.. NextStatuses.Select(s =>
		{
			var t = workflow?.Transition(detail.Node.Status, s);
			return new NextStatusOption(
				s, Runtime.StatusName(detail.Kind, s), t?.RequiresApproval ?? false, t?.EnforceApproval ?? false);
		})];

		// The board's FSM surface for the "View workflow" modal (a few KB — no extra endpoint).
		WorkflowJson = WorkflowGraphJson.Serialize(await _tasks.GetBoardWorkflowAsync(ProjectKey, detail.Board, ct));

		// Use the RESOLVED node id, not the bound NodeId property: on the canonical slug-URL
		// (/tasks/{board}/{slug}) only Board+Slug are bound and NodeId is empty, which would
		// otherwise return an empty thread (comments/spec_plan vanish).
		var comments = await _comments.ListForNodeAsync(ProjectKey, detail.Board, detail.Node.NodeId, ct);
		Thread = CommentThread.Flatten(comments);

		// Resolve `[[slug]]` mentions in the body + every comment body in ONE batch, so the
		// renderer can turn resolvable mentions into node links (node-ref-autolink).
		var bodies = comments.Select(c => (string?)c.Body).Prepend(detail.Node.Body).ToList();
		NodeRefs = await NodeRefMap.BuildAsync(_tasks, WorkspaceKey, ProjectKey, bodies, ct);

		// Same batch shape for memory-entry keys (memory-key-mention-link): one resolution query per
		// container for every candidate key on the page. Off with the Memory feature — nothing to
		// link to then, so the keys stay literal.
		MemoryRefs = _memory is not null && _features.IsEnabled(Feature.Memory)
			? await MemoryRefMap.BuildAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, bodies, ct)
			: MemoryRefs;

		// live-verification finding: resolve the regression banner's fixed-by target to a slug
		// (ObservationFixedByResolver) — a single-node point read, same cost posture as NodeRefs/
		// MemoryRefs just above. No-op (null) when there's no Observation or no FixedByNodeId.
		ObservationFixedByLink = await ObservationFixedByResolver.ResolveAsync(_tasks, ProjectKey, detail.Node.Observation?.FixedByNodeId, ct);
		return Page();
	}
}

// One legal next status for the node page's status select, with the approval gate declared on the
// transition that reaches it. The wording lives here rather than in the Razor so the modal's edge
// labels (ts/workflow-viz.ts gateLabel/gateTooltip) and this select stay phrased the same way — one
// vocabulary for one concept, whichever surface the user meets first.
public sealed record NextStatusOption(string Slug, string Name, bool RequiresApproval, bool EnforceApproval)
{
	// "" (no gate) | "enforced" | "convention" — also the option's data attribute, so a test can
	// assert the marker without matching prose.
	public string GateMode => !RequiresApproval ? string.Empty : EnforceApproval ? "enforced" : "convention";

	// Suffix appended to the option text. Visible in the closed select too, which is the point:
	// the gate must be readable before the move, not only in the dropdown.
	public string GateMarker => GateMode switch
	{
		"enforced" => " · approve (enforced)",
		"convention" => " · approve (not enforced)",
		_ => string.Empty,
	};

	public string GateTitle => GateMode switch
	{
		"enforced" => "Approval gate: owner-only AND enforced — the server blocks this move for anyone who cannot approve.",
		"convention" => "Approval gate by convention: owner-only, but the server does NOT block it — an agent can push this move too.",
		_ => string.Empty,
	};
}
