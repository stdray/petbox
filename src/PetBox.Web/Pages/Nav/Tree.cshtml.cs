using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Data;
using PetBox.Data.Schema;
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
	readonly ICoreDbFactory _f;
	readonly INavigationContext _nav;
	readonly FeatureFlags _features;
	readonly IDataDbFactory _factory;

	public TreeModel(ICoreDbFactory f, INavigationContext nav, FeatureFlags features, IDataDbFactory factory)
	{
		_f = f;
		_nav = nav;
		_features = features;
		_factory = factory;
	}

	public string Ws { get; private set; } = string.Empty;
	public string ProjectKey { get; private set; } = string.Empty;
	public string DbName { get; private set; } = string.Empty;
	public IReadOnlyList<string> Names { get; private set; } = [];

	// Resolves the project and verifies the caller can see its workspace. Takes the caller's
	// connection rather than opening its own — every caller already holds one for the same request.
	bool CanAccessProject(PetBoxDb core, string projectKey)
	{
		var project = core.Projects.FirstOrDefault(p => p.Key == projectKey);
		if (project is null) return false;
		if (!_nav.AvailableWorkspaces.Any(w => string.Equals(w.Key, project.WorkspaceKey, StringComparison.Ordinal)))
			return false;
		Ws = project.WorkspaceKey;
		ProjectKey = projectKey;
		return true;
	}

	public IActionResult OnGetLogs(string project)
	{
		using var db = _f.Open();
		if (!CanAccessProject(db, project)) return NotFound();
		Names = db.Logs
			.Where(l => l.ProjectKey == project)
			.OrderBy(l => l.Name)
			.Select(l => l.Name)
			.ToList();
		return Partial("_LogNodes", this);
	}

	public IActionResult OnGetDatabases(string project)
	{
		using var db = _f.Open();
		if (!CanAccessProject(db, project)) return NotFound();
		if (!_features.IsEnabled(Feature.Data)) { Names = []; return Partial("_DbNodes", this); }
		Names = db.DataDbs
			.Where(d => d.ProjectKey == project)
			.OrderBy(d => d.Name)
			.Select(d => d.Name)
			.ToList();
		return Partial("_DbNodes", this);
	}

	// NB: the `db` parameter is the DataDb NAME (bound from the request) — it is not a connection.
	// The core connection is `core` here to keep the two apart.
	public async Task<IActionResult> OnGetTablesAsync(string project, string db, CancellationToken ct)
	{
		using var core = _f.Open();
		if (!CanAccessProject(core, project)) return NotFound();
		if (!_features.IsEnabled(Feature.Data)) return NotFound();

		var exists = await core.DataDbs.AnyAsync(
			d => d.ProjectKey == project && d.Name == db, ct);
		if (!exists) return NotFound();
		DbName = db;

		var cs = _factory.GetConnectionString(project, db);
		var names = new List<string>();
		await using (var conn = new SqliteConnection(cs))
		{
			await conn.OpenAsync(ct);
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' "
				+ "AND name NOT LIKE 'sqlite_%' AND name <> @journal ORDER BY name";
			var p = cmd.CreateParameter();
			p.ParameterName = "@journal";
			p.Value = SchemaRunner.JournalTableName;
			cmd.Parameters.Add(p);
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
		}
		Names = names;
		return Partial("_TableNodes", this);
	}
}
