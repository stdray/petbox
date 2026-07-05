using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.ProjectHome;

// Detail for ONE node of a task board, addressed by its stable NodeId alone (board-independent):
// /ui/{ws}/{project}/tasks/node/{nodeId}. Shows the full body (no truncation) + the discussion
// thread — the destination the board's abbreviated rows link to — and lets a human EDIT the node
// (status, title, body) in place. Sibling of TaskBoard; named TaskBoardNode (not PlanNode) so it
// doesn't shadow the PetBox.Tasks.Data.PlanNode record. Every read AND write goes through
// ITasksService / ICommentService — the page never opens the DB context itself, and the edit
// handlers route through UpsertAsync so a UI edit hits the SAME guards (FSM, ideaRef, concurrency)
// as the MCP path (spec edit-respects-guards). NetArchTest enforces the door.
[Authorize(Policy = "WorkspaceMember")]
public sealed class TaskBoardNodeModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;
	readonly ICommentService _comments;
	readonly ISettingsResolver _settings;

	public TaskBoardNodeModel(FeatureFlags features, ITasksService tasks, ICommentService comments, ISettingsResolver settings)
	{
		_features = features;
		_tasks = tasks;
		_comments = comments;
		_settings = settings;
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
	// The node's discussion thread, DFS-flattened (shared shape with the board page).
	public IReadOnlyList<CommentLine> Thread { get; private set; } = [];
	// Legal next statuses from the node's current status (edit-status). The status form offers
	// only these; an empty list means no transition is available (e.g. a terminal status).
	public IReadOnlyList<string> NextStatuses { get; private set; } = [];
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

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await LoadAsync(ct);
	}

	// edit-status: move the node to a new status. The select offers only NextStatuses, but the
	// service re-validates the transition (and runs the FSM effects) — the UI can't bypass it.
	public async Task<IActionResult> OnPostStatusAsync(string status, long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await ApplyAsync(p => p with { Status = status }, version, ct);
	}

	// edit-title-body: replace the node's title and body (markdown). An empty field clears it.
	public async Task<IActionResult> OnPostEditAsync(string? title, string? body, long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		return await ApplyAsync(p => p with { Title = title ?? string.Empty, Body = body ?? string.Empty }, version, ct);
	}

	// comments-ui-edit: add a comment (or, when parentId is set, a reply) under this node. Goes
	// through ICommentService.AddAsync — the SAME door the comments_create MCP tool uses — so
	// the reply-parent / same-thread guard applies identically here. `nodeId` rides along on the
	// form as a hidden field (shared markup with the board page) but is ignored here: the node
	// is always the one this page already resolves itself, never client-supplied.
	public async Task<IActionResult> OnPostCommentAddAsync(string? parentId, string body, CancellationToken ct)
	{
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
			var outcome = await _tasks.UpsertAsync(ProjectKey, detail.Board, [patch], TasksActor.Approver, ct);
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

		// Resolve the legal next statuses through the SAME runtime the MCP path uses, keyed by
		// the board's resolved kind — so a definition-declared kind offers transitions from its
		// OWN FSM instead of the `Simple` fallback ParseKind would collapse a custom slug to.
		Runtime = await _tasks.GetRuntimeAsync(ProjectKey, ct);

		// Project-scoped commit-view template (cascades to workspace/system); empty when unset.
		CommitUrlTemplate = (await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey, ct)).CommitUrlTemplate;

		var type = detail.Node.Type.Length == 0 ? null : detail.Node.Type;
		NextStatuses = Runtime.For(detail.Kind, type)?.NextFrom(detail.Node.Status) ?? [];

		// The board's FSM surface for the "View workflow" modal (a few KB — no extra endpoint).
		WorkflowJson = WorkflowGraphJson.Serialize(await _tasks.GetBoardWorkflowAsync(ProjectKey, detail.Board, ct));

		// Use the RESOLVED node id, not the bound NodeId property: on the canonical slug-URL
		// (/tasks/{board}/{slug}) only Board+Slug are bound and NodeId is empty, which would
		// otherwise return an empty thread (comments/spec_plan vanish).
		var comments = await _comments.ListForNodeAsync(ProjectKey, detail.Board, detail.Node.NodeId, ct);
		Thread = CommentThread.Flatten(comments);

		// Resolve `[[slug]]` mentions in the body + every comment body in ONE batch, so the
		// renderer can turn resolvable mentions into node links (node-ref-autolink).
		NodeRefs = await NodeRefMap.BuildAsync(_tasks, WorkspaceKey, ProjectKey,
			comments.Select(c => c.Body).Prepend(detail.Node.Body), ct);
		return Page();
	}
}
