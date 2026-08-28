using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.ProjectHome;

// The human door onto the owner-away digest (/ui/{ws}/{project}/digest/{board}) — the twin of the
// `tasks_owner_digest` MCP verb.
//
// IT BUILDS NOTHING. Every section comes from IOwnerDigestService.DigestAsync, the SAME call the MCP
// verb makes, because the owner's decision was "an MCP tool AND a page, layered on one service": a
// page that assembled its own digest would be a second definition of "waiting on me", and the first
// thing it would do is disagree with the agent-facing one. The only thing this model owns is the
// query-string surface (period, timeline, size) and resolving the project.
//
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass — the same
// posture as every other ProjectHome page; a bare [Authorize] would let any signed-in user read
// another tenant's decision queue by typing the URL.
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class OwnerDigestModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly IOwnerDigestService _digest;

	public OwnerDigestModel(IProjectDirectory projects, FeatureFlags features, IOwnerDigestService digest)
	{
		_projects = projects;
		_features = features;
		_digest = digest;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "board")]
	public string Board { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "days")]
	public int? Days { get; set; }

	[BindProperty(SupportsGet = true, Name = "sinceVersion")]
	public long? SinceVersion { get; set; }

	[BindProperty(SupportsGet = true, Name = "timeline")]
	public bool Timeline { get; set; }

	[BindProperty(SupportsGet = true, Name = "size")]
	public int? Size { get; set; }

	public int EffectiveDays => Days is > 0 ? Days.Value : OwnerDigestRequest.DefaultDays;
	public int EffectiveSize => Size is > 0 ? Size.Value : OwnerDigestRequest.DefaultSectionLimit;

	public Project? Project { get; private set; }
	public bool TasksEnabled => _features.IsEnabled(Feature.Tasks);
	public OwnerDigestView? Digest { get; private set; }
	public string? Error { get; private set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		// The route workspace is welded into the lookup — the field IDOR (/ui/$system/$ws-other/…)
		// that filtering after the fact used to allow.
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !TasksEnabled || string.IsNullOrWhiteSpace(Board)) return;

		try
		{
			Digest = await _digest.DigestAsync(ProjectKey, new OwnerDigestRequest
			{
				Board = Board,
				SinceVersion = SinceVersion,
				Days = EffectiveDays,
				IncludeTimeline = Timeline,
				SectionLimit = EffectiveSize,
			}, urlPrefix: null, ct);
		}
		catch (InvalidOperationException ex)
		{
			// A board that does not exist is the one failure a URL can express here. Surfaced as a
			// message rather than a 500 — the digest page is a place people arrive by typing.
			Error = ex.Message;
		}
	}

	// Links stay RELATIVE here (urlPrefix is null above): the page already knows its own workspace
	// and project, and an absolute permalink is what the MCP verb's includeUrl is for.
	public string NodeHref(OwnerDigestItem item) =>
		Routes.TaskBoardNodeBySlug(WorkspaceKey, ProjectKey, Board, item.Key);

	public string SelfHref(int days, bool timeline, int size) =>
		$"{Routes.ProjectOwnerDigest(WorkspaceKey, ProjectKey, Board)}?days={days}&timeline={timeline.ToString().ToLowerInvariant()}&size={size}";
}
