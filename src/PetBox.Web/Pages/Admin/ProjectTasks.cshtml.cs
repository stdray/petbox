using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Data;

namespace PetBox.Web.Pages.Admin;

// Lists and manages the named task boards of a project (create / delete).
// Mirrors ProjectLogs / ProjectData. Boards are also created by agents via the
// MCP tasks tools (tasks:write); this is the human-facing equivalent.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectTasksModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly ITaskBoardStore _store;

	public ProjectTasksModel(PetBoxDb db, FeatureFlags features, ITaskBoardStore store)
	{
		_db = db;
		_features = features;
		_store = store;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public List<TaskBoardMeta> Boards { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!_features.IsEnabled(Feature.Tasks))
			return NotFound();

		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);
		if (project is null) { ProjectNotFound = true; return Page(); }

		Boards = [.. await _store.ListAsync(ProjectKey)];
		return Page();
	}

	public async Task<IActionResult> OnPostCreateAsync(string name, string? description)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		try
		{
			await _store.CreateAsync(ProjectKey, name?.Trim() ?? string.Empty, description);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			ErrorMessage = ex.Message;
			await OnGetAsync();
			return Page();
		}

		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(string name)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		await _store.DeleteAsync(ProjectKey, name);
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostCloseAsync(string name, bool closed)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		await _store.SetClosedAsync(ProjectKey, name, closed);
		return RedirectToPage();
	}
}
