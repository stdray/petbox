using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Data.Contract;
using PetBox.Log.Core.Data;
using PetBox.Web.Auth;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages.Nav;

// htmx lazy-children endpoints for the sidebar tree. Each handler returns a small
// partial of <li> nodes loaded on first expand (hx-trigger="toggle once").
//
// Unified authz: every handler resolves the requested project's workspace and
// checks it against the caller's membership (Nav.AvailableWorkspaces) — one
// gate, no per-handler copy-paste, closes the IDOR the plan calls out.
[Authorize]
public sealed class TreeModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly ILogStore _logStore;
	readonly IDataDbCatalog _dataDbs;
	readonly INavigationContext _nav;
	readonly FeatureFlags _features;

	public TreeModel(
		IProjectDirectory projects, ILogStore logStore, IDataDbCatalog dataDbs,
		INavigationContext nav, FeatureFlags features)
	{
		_projects = projects;
		_logStore = logStore;
		_dataDbs = dataDbs;
		_nav = nav;
		_features = features;
	}

	public string Ws { get; private set; } = string.Empty;
	public string ProjectKey { get; private set; } = string.Empty;
	public string DbName { get; private set; } = string.Empty;
	public IReadOnlyList<string> Names { get; private set; } = [];

	// Resolves the project and verifies the caller can see its workspace. core.db is visible only in
	// the service layer now (db-out-of-pages-remaining-24), so this asks IProjectDirectory instead of
	// sharing a connection a caller already held — the cost of that is one extra core.db open per
	// handler call; see the conversion's report for the count.
	async Task<bool> CanAccessProjectAsync(string projectKey, CancellationToken ct)
	{
		var project = await _projects.GetAsync(projectKey, ct);
		if (project is null) return false;
		if (!_nav.AvailableWorkspaces.Any(w => string.Equals(w.Key, project.WorkspaceKey, StringComparison.Ordinal)))
			return false;
		Ws = project.WorkspaceKey;
		ProjectKey = projectKey;
		return true;
	}

	public async Task<IActionResult> OnGetLogsAsync(string project, CancellationToken ct)
	{
		if (!await CanAccessProjectAsync(project, ct)) return NotFound();
		Names = [.. (await _logStore.ListAsync(project, ct)).Select(l => l.Name)];
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
