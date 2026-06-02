using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Memory.Contract;

namespace PetBox.Web.Pages.Admin;

// Lists and manages the named memory stores of a project (create / delete).
// Mirrors ProjectLogs / ProjectData. Stores are also created by agents via the
// MCP memory tools (memory:write); this is the human-facing equivalent.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectMemoryModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly IMemoryService _memory;

	public ProjectMemoryModel(PetBoxDb db, FeatureFlags features, IMemoryService memory)
	{
		_db = db;
		_features = features;
		_memory = memory;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public List<MemoryStoreMeta> Stores { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!_features.IsEnabled(Feature.Memory))
			return NotFound();

		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);
		if (project is null) { ProjectNotFound = true; return Page(); }

		Stores = [.. await _memory.ListStoresAsync(ProjectKey)];
		return Page();
	}

	public async Task<IActionResult> OnPostCreateAsync(string name, string? description)
	{
		if (!_features.IsEnabled(Feature.Memory)) return NotFound();

		try
		{
			await _memory.CreateStoreAsync(ProjectKey, name?.Trim() ?? string.Empty, description);
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
		if (!_features.IsEnabled(Feature.Memory)) return NotFound();

		await _memory.DeleteStoreAsync(ProjectKey, name);
		return RedirectToPage();
	}
}
