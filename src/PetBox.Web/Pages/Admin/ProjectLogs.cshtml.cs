using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;

namespace PetBox.Web.Pages.Admin;

// Lists and manages the named logs of a project (create / delete). Mirrors
// ProjectData (DataDbs). The petbox self-log is shown but cannot be deleted.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectLogsModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly ILogStore _store;

	public ProjectLogsModel(PetBoxDb db, FeatureFlags features, ILogStore store)
	{
		_db = db;
		_features = features;
		_store = store;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public List<LogMeta> Logs { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	public bool IsSelfLog(LogMeta log) =>
		log.ProjectKey == LogNames.SystemProject && log.Name == LogNames.SelfLog;

	public async Task<IActionResult> OnGetAsync()
	{
		if (!_features.IsEnabled(Feature.Logging))
			return NotFound();

		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);
		if (project is null) { ProjectNotFound = true; return Page(); }

		Logs = [.. await _store.ListAsync(ProjectKey)];
		return Page();
	}

	public async Task<IActionResult> OnPostCreateAsync(string name, string? description)
	{
		if (!_features.IsEnabled(Feature.Logging)) return NotFound();

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
		if (!_features.IsEnabled(Feature.Logging)) return NotFound();

		if (ProjectKey == LogNames.SystemProject && name == LogNames.SelfLog)
		{
			ErrorMessage = "The petbox self-log cannot be deleted.";
			await OnGetAsync();
			return Page();
		}

		await _store.DeleteAsync(ProjectKey, name);
		this.NotifySuccess($"Log '{name}' deleted.");
		return RedirectToPage();
	}
}
