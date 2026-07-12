using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI tasks dashboard for a project (/ui/{ws}/{project}/tasks). Read-only list
// of named boards from petbox.db metadata (cheap; no per-board file opens).
// Boards are created by agents via the MCP tasks tools.
[Authorize]
public sealed class TasksModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;

	public TasksModel(ICoreDbFactory f, FeatureFlags features, ITasksService tasks)
	{
		_f = f;
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

	public async Task OnGetAsync(CancellationToken ct)
	{
		using var db = _f.Open();
		Project = await db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		if (Project is null || !TasksEnabled) return;

		Boards = await _tasks.ListBoardsAsync(ProjectKey, ct);
	}
}
