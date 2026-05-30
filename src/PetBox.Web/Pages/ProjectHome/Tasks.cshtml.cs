using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Data;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI tasks dashboard for a project (/ui/{ws}/{project}/tasks). Read-only list
// of named boards from petbox.db metadata (cheap; no per-board file opens).
// Boards are created by agents via the MCP tasks tools.
[Authorize]
public sealed class TasksModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly ITaskBoardStore _store;

	public TasksModel(PetBoxDb db, FeatureFlags features, ITaskBoardStore store)
	{
		_db = db;
		_features = features;
		_store = store;
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
		Project = await _db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		if (Project is null || !TasksEnabled) return;

		Boards = await _store.ListAsync(ProjectKey, ct);
	}
}
