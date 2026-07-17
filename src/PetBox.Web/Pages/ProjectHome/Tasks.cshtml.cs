using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI tasks dashboard for a project (/ui/{ws}/{project}/tasks). Read-only list
// of named boards from petbox.db metadata (cheap; no per-board file opens).
// Boards are created by agents via the MCP tasks tools.
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class TasksModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;

	public TasksModel(IProjectDirectory projects, FeatureFlags features, ITasksService tasks)
	{
		_projects = projects;
		_features = features;
		_tasks = tasks;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Project? Project { get; private set; }
	public bool TasksEnabled => _features.IsEnabled(Feature.Tasks);
	public IReadOnlyList<TaskBoardMeta> Boards { get; private set; } = [];

	// spec methodology-inactive-visibility: the project's current effective default instance —
	// a board whose own membership names an open instance other than this one is a full member
	// of a live process that just isn't the project's default right now. Computed here (not a
	// stored board flag) so the card template can compare identity directly.
	public string? EffectiveActiveInstance { get; private set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		// The route workspace is welded into the lookup: ProjectWorkspaceBindingFilter has already
		// 404'd a project of another workspace before this runs, and this is the second rubicon — a
		// page that asked by key alone is how the {ws}/{project} IDOR class got in (see ProjectHome/Index).
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !TasksEnabled) return;

		Boards = await _tasks.ListBoardsAsync(ProjectKey, ct);
		EffectiveActiveInstance = await _tasks.ResolveDefaultMethodologyInstanceAsync(ProjectKey, ct);
	}
}
