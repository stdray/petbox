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
		Project = await _db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		if (Project is null || !MemoryEnabled) return;

		Stores = await _memory.ListStoresAsync(ProjectKey, ct);
	}
}
