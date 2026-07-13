using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Data.Contract;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI databases dashboard for a project (/ui/{ws}/{project}/databases).
// Read-only list from petbox.db metadata (cheap; no SQLite file opens).
// Create/delete + schema live in the admin area (reached via the gear).
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class DatabasesModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly IDataDbCatalog _catalog;

	public DatabasesModel(IProjectDirectory projects, FeatureFlags features, IDataDbCatalog catalog)
	{
		_projects = projects;
		_features = features;
		_catalog = catalog;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Core.Models.Project? Project { get; private set; }
	public bool DataEnabled => _features.IsEnabled(Feature.Data);
	// Databases are created in the workspace-admin Data page — only surface the create link to
	// viewers who can actually reach it.
	public bool CanAdminWorkspace { get; private set; }
	public IReadOnlyList<DataDbInfo> Dbs { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		CanAdminWorkspace = User.CanAdminWorkspace(WorkspaceKey);
		// The route workspace is welded into the lookup — the second rubicon behind
		// ProjectWorkspaceBindingFilter, not a replacement for it (see ProjectHome/Index).
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !DataEnabled) return;

		Dbs = await _catalog.ListAsync(ProjectKey, ct);
	}
}
