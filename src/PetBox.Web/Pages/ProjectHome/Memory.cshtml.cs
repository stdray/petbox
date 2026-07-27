using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Memory.Contract;
using PetBox.Web.Auth;
using PetBox.Web.Memory;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI memory dashboard for a project (/ui/{ws}/{project}/memory). With no `q`, a read-only
// list of named stores from petbox.db metadata (unchanged). A non-empty `q` sweeps the WHOLE
// project's memory (every store, no Filter.Store — the UI twin of `memory_search` with no
// `store` arg: spec search-one-engine-for-human-and-agent) through the same hybrid engine the
// per-store page and MCP both use (IMemoryService.SearchEntriesAsync via MemorySearchScope),
// filterable by type, scoped project/workspace/cascade, sorted relevance/created/updated. This
// is the ONE place a search can span every store at once — the per-store page can only ever
// narrow to the store it is already on. Stores are created by agents via the memory MCP tools.
// WorkspaceViewer: route workspaceKey membership (sysadmin free-pass) — closes
// cross-tenant shared-memory reads that bare [Authorize] allowed.
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class MemoryModel : PageModel
{
	readonly IWorkspaceMemoryDirectory _workspaceMemory;
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly IMemoryService _memory;
	// Optional ctor param, same posture as TaskBoardNodeModel's _catalog: needed only to gate
	// MemorySearchScope's derived workspace-container leg (SandboxContainment.PermitsAsync) — DI
	// always supplies it (IProjectCatalog is registered unconditionally), a bare unit-test
	// construction that never exercises a workspace/cascade search may omit it.
	readonly IProjectCatalog? _catalog;

	public MemoryModel(
		IWorkspaceMemoryDirectory workspaceMemory, IProjectDirectory projects, FeatureFlags features, IMemoryService memory,
		IProjectCatalog? catalog = null)
	{
		_workspaceMemory = workspaceMemory;
		_projects = projects;
		_features = features;
		_memory = memory;
		_catalog = catalog;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Query { get; set; }

	[BindProperty(SupportsGet = true, Name = "type")]
	public string? Type { get; set; }

	[BindProperty(SupportsGet = true, Name = "scope")]
	public string? Scope { get; set; }

	[BindProperty(SupportsGet = true, Name = "sort")]
	public string? Sort { get; set; }

	public bool ScopeSelectable => !WorkspaceMemory.IsWorkspaceContainer(ProjectKey);

	const int SearchLimit = 40;

	public Project? Project { get; private set; }
	public bool MemoryEnabled => _features.IsEnabled(Feature.Memory);
	public IReadOnlyList<MemoryStoreMeta> Stores { get; private set; } = [];

	public bool IsSearch { get; private set; }
	public IReadOnlyList<MemorySearchScope.Row> Hits { get; private set; } = [];
	public SearchRetrievers? Retrievers { get; private set; }
	public IReadOnlyDictionary<string, MemoryUsageView> HitUsage { get; private set; } =
		new Dictionary<string, MemoryUsageView>();

	public async Task OnGetAsync(CancellationToken ct)
	{
		// Shared-memory routes (/ui/{ws}/$ws-{ws}/memory or /ui/$system/$workspace/memory):
		// lazy-ensure the container so the first UI navigation is not a "Project not found"
		// before any MCP write. No-op when the row already exists (incl. M028 $workspace).
		//
		// Provisioning goes through IWorkspaceMemoryDirectory — the page no longer opens core.db
		// itself; EnsureWorkspaceContainerAsync is idempotent under concurrent GETs (see its doc).
		if (WorkspaceMemory.IsWorkspaceContainer(ProjectKey)
			&& string.Equals(WorkspaceMemory.WorkspaceKeyOfContainer(ProjectKey), WorkspaceKey, StringComparison.Ordinal))
		{
			await _workspaceMemory.EnsureWorkspaceContainerAsync(WorkspaceKey, ct);
		}

		// The route workspace is welded into the lookup — this is the field IDOR
		// (/ui/$system/$ws-other/memory) the page used to reject by filtering after the fact.
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !MemoryEnabled) return;

		Stores = await _memory.ListStoresAsync(ProjectKey, ct);

		var q = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim();
		IsSearch = q is not null;
		if (!IsSearch) return;

		var result = await MemorySearchScope.SearchAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope,
			new SearchRequest<MemoryEntryFilter, MemorySortBy>
			{
				Query = q,
				// No Store filter — sweep every (non-sensitive) store in scope, the project-wide
				// twin of memory_search called with no `store` arg.
				Filter = new MemoryEntryFilter(null, Type),
				Sort = ParseSortBy(Sort),
				Limit = SearchLimit,
				BodyLen = 240, // a snippet — this view lists across stores, not one store's full cards
			}, ct);
		Hits = result.Rows;
		Retrievers = result.Retrievers;
		HitUsage = await MemorySearchScope.LoadUsageAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope, Hits, ct);
	}

	static (MemorySortBy By, bool Desc)? ParseSortBy(string? sort) => sort?.Trim().ToLowerInvariant() switch
	{
		"created" => (MemorySortBy.Created, true),
		"updated" => (MemorySortBy.Updated, true),
		_ => null,
	};
}
