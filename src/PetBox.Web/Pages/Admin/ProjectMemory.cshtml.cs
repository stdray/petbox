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

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
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

		// Defense in depth: system stores (e.g. session-digests) are machine plumbing, not user
		// knowledge — the UI hides their Delete button, but reject the POST too so a crafted request
		// can never remove one. `IsSystem` is the canonical store-taxonomy flag (spec: memoverhaul).
		var stores = await _memory.ListStoresAsync(ProjectKey);
		if (stores.Any((MemoryStoreMeta s) => s.IsSystem && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
		{
			ErrorMessage = $"The system store '{name}' cannot be deleted.";
			Stores = [.. stores];
			return Page();
		}

		await _memory.DeleteStoreAsync(ProjectKey, name);
		this.NotifySuccess($"Store '{name}' deleted.");
		return RedirectToPage();
	}
}
