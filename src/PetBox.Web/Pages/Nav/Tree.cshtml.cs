using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Data.Contract;
using PetBox.Log.Core.Contract;
using PetBox.Web.Auth;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages.Nav;

// htmx lazy-children endpoints for the sidebar tree. Each handler returns a small
// partial of <li> nodes loaded on first expand (hx-trigger="toggle once").
[Authorize]
// THE ONE PAGE WHOSE TENANT IS AN ARGUMENT, NOT A ROUTE VALUE. Its route is a bare /ui/_nav/tree with
// no tenant slot at all; the project arrives as a QUERY parameter on every call (_Layout.cshtml and
// _DbNodes.cshtml build `?handler=…&project=…`), and all three handlers below bind it under that exact
// name. TenantSource.Argument is what reads a query string on this plane, so the PEP reads the same
// `project` the handlers do.
//
// This page is bare [Authorize] — no workspace policy, because there is no {workspaceKey} to check one
// against — so the declaration is now the ONLY tenant gate in front of it. That is not a reduction:
// what it replaces is CanAccessProjectAsync's membership test, and it replaces it with the same
// question asked centrally (the project's owning workspace, resolved from the catalog, at Viewer or
// better, sysadmin free pass included).
//
// The cross-tenant probe still scores this page "no tenant slot", correctly: `Addressed` is a question
// about the ROUTE and the route genuinely has nowhere to write a victim's key. The probe reaching it
// through the query string is the Razor blind spot, not a property of this declaration.
[TenantFrom(TenantSource.Argument, "project")]
public sealed class TreeModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly ILogService _logs;
	readonly IDataDbCatalog _dataDbs;
	readonly FeatureFlags _features;

	// INavigationContext is GONE from this page's dependencies, not merely unused: it was injected for
	// one thing — AvailableWorkspaces, the membership test the declaration replaced — and a page holding
	// a sidebar-catalog service it no longer asks anything is how a deleted check gets quietly
	// reinstated. Nothing else here reads it (the tree's own links come off Ws/ProjectKey below).
	public TreeModel(
		IProjectDirectory projects, ILogService logs, IDataDbCatalog dataDbs, FeatureFlags features)
	{
		_projects = projects;
		_logs = logs;
		_dataDbs = dataDbs;
		_features = features;
	}

	public string Ws { get; private set; } = string.Empty;
	public string ProjectKey { get; private set; } = string.Empty;
	public string DbName { get; private set; } = string.Empty;
	public IReadOnlyList<string> Names { get; private set; } = [];

	// Resolves the project and carries its workspace into the partials (the tree's links need `Ws`).
	//
	// WHAT CAME OUT: this method used to end with
	//
	//     if (!_nav.AvailableWorkspaces.Any(w => w.Key == project.WorkspaceKey)) return false;
	//
	// — a hand-written tenant check, and until this commit the only one in front of this page. It is
	// gone, not moved: [TenantFrom(Argument, "project")] on the class asks ITenantAuthorizer the same
	// question (membership of the project's OWNING workspace, resolved from the catalog, at Viewer or
	// better, sysadmin free pass) before any handler here runs. Reinstating it would be a second copy of
	// a centralized decision, which is the thing the declaration exists to remove.
	//
	// WHAT STAYED, and why it is not the same check: the null lookup. It answers "does this project
	// exist", which the tenant axis does not — and it is also where `Ws`/`ProjectKey` come from, so the
	// method has a job beyond returning a bool. core.db stays behind IProjectDirectory
	// (db-out-of-pages-remaining-24); one open per handler call, unchanged.
	async Task<bool> CanAccessProjectAsync(string projectKey, CancellationToken ct)
	{
		var project = await _projects.GetAsync(projectKey, ct);
		if (project is null) return false;
		Ws = project.WorkspaceKey;
		ProjectKey = projectKey;
		return true;
	}

	public async Task<IActionResult> OnGetLogsAsync(string project, CancellationToken ct)
	{
		if (!await CanAccessProjectAsync(project, ct)) return NotFound();
		Names = [.. (await _logs.ListAsync(project, ct)).Select(l => l.Name)];
		return Partial("_LogNodes", this);
	}

	public async Task<IActionResult> OnGetDatabasesAsync(string project, CancellationToken ct)
	{
		if (!await CanAccessProjectAsync(project, ct)) return NotFound();
		if (!_features.IsEnabled(Feature.Data)) { Names = []; return Partial("_DbNodes", this); }
		Names = [.. (await _dataDbs.ListAsync(project, ct)).Select(d => d.Name)];
		return Partial("_DbNodes", this);
	}

	// NB: the `db` parameter is the DataDb NAME (bound from the request) — it is not a connection.
	public async Task<IActionResult> OnGetTablesAsync(string project, string db, CancellationToken ct)
	{
		if (!await CanAccessProjectAsync(project, ct)) return NotFound();
		if (!_features.IsEnabled(Feature.Data)) return NotFound();

		// DescribeAsync proves the catalog row itself (NotFound as null) before touching the data
		// file, so this replaces both the old existence check AND the sqlite_master table scan —
		// IDataDbCatalog already owns exactly this operation (Admin/ProjectData's db_describe path).
		var tables = await _dataDbs.DescribeAsync(project, db, ct);
		if (tables is null) return NotFound();
		DbName = db;
		Names = [.. tables.Select(t => t.Name)];
		return Partial("_TableNodes", this);
	}
}
