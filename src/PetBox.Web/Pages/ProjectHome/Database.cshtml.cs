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
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI single-database view (/ui/{ws}/{project}/databases/{dbName}).
// Read-only table list with on-page row counts (one SQLite file open — allowed
// on a leaf page per the IA perf invariants; never in the sidebar/aggregate).
// Schema migrations + create/delete stay in admin (gear → project → Data).
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class DatabaseModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly IDataDbFactory _factory;

	public DatabaseModel(ICoreDbFactory f, IProjectDirectory projects, FeatureFlags features, IDataDbFactory factory)
	{
		_f = f;
		_projects = projects;
		_features = features;
		_factory = factory;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "dbName")]
	public string DbName { get; set; } = string.Empty;

	public Core.Models.Project? Project { get; private set; }
	public DataDb? Db { get; private set; }
	public bool DataEnabled => _features.IsEnabled(Feature.Data);
	public IReadOnlyList<TableRow> Tables { get; private set; } = [];

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!DataEnabled) return Page();

		// The route workspace is welded into the lookup — the second rubicon behind
		// ProjectWorkspaceBindingFilter, not a replacement for it (see ProjectHome/Index).
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null) return Page();

		// The data-db catalog has no service door yet — this page still reads it itself.
		using var db = _f.Open();
		Db = await db.DataDbs.FirstOrDefaultAsync(
			d => d.ProjectKey == ProjectKey && d.Name == DbName, ct);
		if (Db is null) return Page();

		Tables = await IntrospectAsync(ct);
		return Page();
	}

	async Task<IReadOnlyList<TableRow>> IntrospectAsync(CancellationToken ct)
	{
		var cs = _factory.GetConnectionString(ProjectKey, DbName);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync(ct);

		var names = new List<string>();
		await using (var cmd = conn.CreateCommand())
		{
			cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' "
				+ "AND name NOT LIKE 'sqlite_%' AND name <> @journal ORDER BY name";
			var p = cmd.CreateParameter();
			p.ParameterName = "@journal";
			p.Value = SchemaRunner.JournalTableName;
			cmd.Parameters.Add(p);
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
		}

		var rows = new List<TableRow>(names.Count);
		foreach (var name in names)
		{
			var cols = new List<string>();
			await using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = $"PRAGMA table_info(\"{name.Replace("\"", "\"\"")}\")";
				await using var reader = await cmd.ExecuteReaderAsync(ct);
				while (await reader.ReadAsync(ct))
					cols.Add(reader.GetString(1) + " " + reader.GetString(2));
			}

			long count;
			await using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"";
				count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
			}

			rows.Add(new TableRow(name, cols, count));
		}
		return rows;
	}

	public sealed record TableRow(string Name, IReadOnlyList<string> Columns, long RowCount);
}
