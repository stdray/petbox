using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Memory.Contract;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI memory dashboard for a project (/ui/{ws}/{project}/memory). Read-only
// list of named stores from petbox.db metadata. v1 is project-scoped. Stores are
// created by agents via the memory MCP tools.
[Authorize]
public sealed class MemoryModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly IMemoryService _memory;

	public MemoryModel(PetBoxDb db, FeatureFlags features, IMemoryService memory)
	{
		_db = db;
		_features = features;
		_memory = memory;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Project? Project { get; private set; }
	public bool MemoryEnabled => _features.IsEnabled(Feature.Memory);
	public IReadOnlyList<MemoryStoreMeta> Stores { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		// Shared-memory routes (/ui/{ws}/$ws-{ws}/memory or /ui/$system/$workspace/memory):
		// lazy-ensure the container so the first UI navigation is not a "Project not found"
		// before any MCP write. No-op when the row already exists (incl. M028 $workspace).
		if (WorkspaceMemory.IsWorkspaceContainer(ProjectKey)
			&& string.Equals(WorkspaceMemory.WorkspaceKeyOfContainer(ProjectKey), WorkspaceKey, StringComparison.Ordinal))
			await WorkspaceMemory.EnsureContainerAsync(_db, WorkspaceKey, ct);

		Project = await _db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		// Bind project to route workspace — reject IDOR (/ui/other-ws/$workspace/memory etc.).
		if (Project is not null && !string.Equals(Project.WorkspaceKey, WorkspaceKey, StringComparison.Ordinal))
			Project = null;
		if (Project is null || !MemoryEnabled) return;

		Stores = await _memory.ListStoresAsync(ProjectKey, ct);
	}
}
