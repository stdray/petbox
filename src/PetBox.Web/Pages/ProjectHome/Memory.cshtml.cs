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
// WorkspaceViewer: route workspaceKey membership (sysadmin free-pass) — closes
// cross-tenant shared-memory reads that bare [Authorize] allowed.
[Authorize(Policy = "WorkspaceViewer")]
public sealed class MemoryModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly FeatureFlags _features;
	readonly IMemoryService _memory;

	public MemoryModel(ICoreDbFactory f, FeatureFlags features, IMemoryService memory)
	{
		_f = f;
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
		using var db = _f.Open();
		// Shared-memory routes (/ui/{ws}/$ws-{ws}/memory or /ui/$system/$workspace/memory):
		// lazy-ensure the container so the first UI navigation is not a "Project not found"
		// before any MCP write. No-op when the row already exists (incl. M028 $workspace).
		if (WorkspaceMemory.IsWorkspaceContainer(ProjectKey)
			&& string.Equals(WorkspaceMemory.WorkspaceKeyOfContainer(ProjectKey), WorkspaceKey, StringComparison.Ordinal))
			await WorkspaceMemory.EnsureContainerAsync(db, WorkspaceKey, ct);

		Project = await db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		// Bind project to route workspace — reject field IDOR (/ui/$system/$ws-other/memory).
		// Membership of route workspace is enforced by WorkspaceViewer policy above.
		if (Project is not null && !string.Equals(Project.WorkspaceKey, WorkspaceKey, StringComparison.Ordinal))
			Project = null;
		if (Project is null || !MemoryEnabled) return;

		Stores = await _memory.ListStoresAsync(ProjectKey, ct);
	}
}
