using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Core.Health;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

// The project settings page. It holds NO db factory and opens NO connection: every read and write
// goes through a service (AGENTS.md, "the database is visible only in the service layer"; work
// `db-out-of-pages-into-services'). The surfaces it needs, and who owns each:
//
//   projects          -> IProjectDirectory
//   api keys          -> AgentKeyAdminService      (project-confined ListByProjectAsync, for the count only —
//                         create/revoke/edit-scopes moved to ProjectKeys/Routes.ProjectKeys, admin-routes-and-pages item 3)
//   health endpoints  -> IHealthEndpointDirectory  (the pull-mode source list)
//   settings          -> ISettingsResolver (the cascade) — log retention (LogSettings.RetentionDays)
//                         moved to the generic project Settings page (item 3); this page keeps only
//                         RepoSettings.CommitUrlTemplate, which SettingsScopePolicy deliberately excludes
//                         from the generic pages to avoid two disagreeing edit surfaces.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectDetailModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly AgentKeyAdminService _keys;
	readonly IHealthEndpointDirectory _health;
	readonly FeatureFlags _features;
	readonly ISettingsResolver _settings;

	public ProjectDetailModel(
		IProjectDirectory projects,
		AgentKeyAdminService keys,
		IHealthEndpointDirectory health,
		FeatureFlags features,
		ISettingsResolver settings)
	{
		_projects = projects;
		_keys = keys;
		_health = health;
		_features = features;
		_settings = settings;
	}

	public bool DataEnabled => _features.IsEnabled(Feature.Data);

	// The project's effective commit-view URL template (RepoSettings, cascaded). Empty = unset →
	// commit refs/hashes render as plain text. A literal {sha} placeholder is expanded per commit.
	public string CommitUrlTemplate { get; private set; } = "";

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	// Back-compat alias for the old testid/template that referenced `Model.Key`.
	public string Key => ProjectKey;

	public Project? Project { get; private set; }
	public IReadOnlyList<HealthEndpoint> HealthEndpoints { get; private set; } = [];

	// admin-routes-and-pages item 3: key MANAGEMENT (create/list/revoke/edit-scopes) moved to its
	// own page (ProjectKeys/Routes.ProjectKeys) — this page only shows a count + a link there, so
	// it stops being the single overloaded screen for everything project-scoped.
	public int KeyCount { get; private set; }

	public string? ErrorMessage { get; set; }

	public async Task OnGetAsync()
	{
		Project = await _projects.GetAsync(ProjectKey);
		if (Project is null) return;

		HealthEndpoints = await _health.ListForProjectAsync(ProjectKey);
		KeyCount = (await _keys.ListByProjectAsync(ProjectKey)).Count;

		// Effective commit-view template via the cascade (project → workspace → system). Log
		// retention moved to the generic project Settings page (SettingsScopePolicy already renders
		// LogSettings there) — this is the only settings cascade this page still owns.
		CommitUrlTemplate = (await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey)).CommitUrlTemplate;
	}

	// Set (or clear, when blank) the per-project commit-view URL template. An empty text input
	// binds to null, so normalize to "" before comparing/writing — a blank submit resets the
	// override so the project falls back up the cascade.
	public async Task<IActionResult> OnPostSetCommitTemplateAsync(string? commitUrlTemplate)
	{
		var trimmed = (commitUrlTemplate ?? string.Empty).Trim();
		var oldSettings = await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey);

		if (trimmed.Length == 0)
		{
			await _settings.ResetAsync<RepoSettings>(Scope.Project, ProjectKey, nameof(RepoSettings.CommitUrlTemplate));
			this.NotifySuccess("Commit template cleared.");
			return Self();
		}

		var newSettings = oldSettings with { CommitUrlTemplate = trimmed };
		var userIdRaw = User.FindFirst(PetBox.Core.Auth.PetBoxClaims.UserId)?.Value;
		long? userId = long.TryParse(userIdRaw, out var uid) ? uid : null;
		await _settings.SetAsync(Scope.Project, ProjectKey, newSettings, oldSettings, userId);
		this.NotifySuccess("Commit template saved.");
		return Self();
	}

	// Drop the project's own commit-view override so it falls back up the cascade.
	public async Task<IActionResult> OnPostClearCommitTemplateAsync()
	{
		await _settings.ResetAsync<RepoSettings>(Scope.Project, ProjectKey, nameof(RepoSettings.CommitUrlTemplate));
		this.NotifySuccess("Commit template cleared.");
		return Self();
	}

	RedirectResult Self() => Redirect(Routes.ProjectSettings(WorkspaceKey, ProjectKey));

	public async Task<IActionResult> OnPostCreateHealthEndpointAsync(string url, int? intervalSeconds)
	{
		// The URL rule and the interval floor live in the directory — they are properties of what the
		// poller can honour, not of this form.
		var result = await _health.AddAsync(ProjectKey, url, intervalSeconds, User.Identity?.Name);
		if (result is HealthEndpointAddResult.Refused refused)
		{
			ErrorMessage = refused.Reason;
			await OnGetAsync();
			return Page();
		}

		this.NotifySuccess("Health endpoint added.");
		return Self();
	}

	public async Task<IActionResult> OnPostDeleteHealthEndpointAsync(long id)
	{
		// The project is welded into the DELETE inside the directory — a forged id belonging to another
		// project matches nothing.
		await _health.DeleteAsync(id, ProjectKey);
		this.NotifySuccess("Health endpoint deleted.");
		return Self();
	}

	// Delete the project and everything it owns in the Core DB (keys, health endpoints,
	// data/log/board/memory metadata, relations, settings). The cascade, the reserved-project
	// refusal and the workspace ownership check all live in IProjectDirectory.DeleteAsync — the
	// workspace is part of the ADDRESS there, so a forged POST naming another tenant's project
	// matches nothing.
	public async Task<IActionResult> OnPostDeleteAsync()
	{
		var result = await _projects.DeleteAsync(WorkspaceKey, ProjectKey);

		switch (result)
		{
			case ProjectChangeResult.Refused refused:
				ErrorMessage = refused.Reason;
				await OnGetAsync();
				return Page();
			case ProjectChangeResult.NotFound:
				ErrorMessage = "Project not found.";
				await OnGetAsync();
				return Page();
			default:
				this.NotifySuccess($"Project '{ProjectKey}' deleted.");
				return Redirect(Routes.WorkspaceAdminProjects(WorkspaceKey));
		}
	}
}
