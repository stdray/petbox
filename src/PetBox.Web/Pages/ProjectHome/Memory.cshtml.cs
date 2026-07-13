using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Memory.Contract;
using PetBox.Web.Auth;

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
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly IMemoryService _memory;

	public MemoryModel(ICoreDbFactory f, IProjectDirectory projects, FeatureFlags features, IMemoryService memory)
	{
		_f = f;
		_projects = projects;
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
		//
		// The one core.db open left on this page, and it is opened INSIDE the container branch — a
		// normal project page never reaches it. Provisioning a container has no service door yet
		// (WorkspaceMemory is the writer everyone shares); when it grows one, this goes through it and
		// the page stops seeing the database at all.
		if (WorkspaceMemory.IsWorkspaceContainer(ProjectKey)
			&& string.Equals(WorkspaceMemory.WorkspaceKeyOfContainer(ProjectKey), WorkspaceKey, StringComparison.Ordinal))
		{
			using var db = _f.Open();
			await WorkspaceMemory.EnsureContainerAsync(db, WorkspaceKey, ct);
		}

		// The route workspace is welded into the lookup — this is the field IDOR
		// (/ui/$system/$ws-other/memory) the page used to reject by filtering after the fact.
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !MemoryEnabled) return;

		Stores = await _memory.ListStoresAsync(ProjectKey, ct);
	}
}
