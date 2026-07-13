using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
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
	readonly ICoreDbFactory _f;
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;

	public DatabasesModel(ICoreDbFactory f, IProjectDirectory projects, FeatureFlags features)
	{
		_f = f;
		_projects = projects;
		_features = features;
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
	public IReadOnlyList<DataDb> Dbs { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		CanAdminWorkspace = User.CanAdminWorkspace(WorkspaceKey);
		// The route workspace is welded into the lookup — the second rubicon behind
		// ProjectWorkspaceBindingFilter, not a replacement for it (see ProjectHome/Index).
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !DataEnabled) return;

		// The data-db catalog has no service door yet — this page still reads it itself.
		using var db = _f.Open();
		Dbs = await db.DataDbs
			.Where(d => d.ProjectKey == ProjectKey)
			.OrderBy(d => d.Name)
			.ToListAsync(ct);
	}
}
