using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using PetBox.Core.Features;
using PetBox.Data;
using PetBox.Data.Contract;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI table view (/ui/{ws}/{project}/databases/{dbName}/{tableName}).
// A read-only SQL bar + paged results. The connection is opened ReadOnly so the
// bar can't mutate data — writes/DDL go through the API (/exec) or admin schema
// apply. One SQLite open per request — a leaf page, allowed per the perf invariants.
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class TableModel : PageModel
{
	const int PageSize = 50;

	readonly FeatureFlags _features;
	readonly IDataDbFactory _factory;
	readonly IDataDbCatalog _catalog;

	public TableModel(FeatureFlags features, IDataDbFactory factory, IDataDbCatalog catalog)
	{
		_features = features;
		_factory = factory;
		_catalog = catalog;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "dbName")]
	public string DbName { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "tableName")]
	public string TableName { get; set; } = string.Empty;

	public bool DataEnabled => _features.IsEnabled(Feature.Data);
	public bool DbNotFound { get; private set; }

	public string Sql { get; private set; } = string.Empty;
	public int PageNum { get; private set; }
	public bool HasNext { get; private set; }
	public IReadOnlyList<string> Columns { get; private set; } = [];
	public IReadOnlyList<IReadOnlyList<string?>> Rows { get; private set; } = [];
	public string? ErrorMessage { get; private set; }

	public string DefaultSql => $"SELECT * FROM \"{TableName.Replace("\"", "\"\"")}\"";

	// NOTE: the paging arg is 'pageNum', not 'page' — 'page' is a reserved
	// route-key in Razor Pages, so a ?page=N query value never binds here.
	public async Task<IActionResult> OnGetAsync(string? sql, int? pageNum, CancellationToken ct)
	{
		if (!DataEnabled) return Page();

		// The project↔workspace binding is enforced by ProjectWorkspaceBindingFilter before this
		// handler runs (see ProjectHome/Index) — the page only resolves the DB within the project.
		var exists = await _catalog.GetAsync(ProjectKey, DbName, ct) is not null;
		if (!exists) { DbNotFound = true; return Page(); }

		Sql = string.IsNullOrWhiteSpace(sql) ? DefaultSql : sql.Trim();
		PageNum = pageNum is > 0 ? pageNum.Value : 0;

		await RunAsync(ct);
		return Page();
	}

	async Task RunAsync(CancellationToken ct)
	{
		var trimmed = Sql.TrimEnd().TrimEnd(';');
		var offset = PageNum * PageSize;
		// Wrap the user's SELECT so paging is uniform regardless of their own LIMIT.
		var paged = $"SELECT * FROM (\n{trimmed}\n) AS _q LIMIT {PageSize + 1} OFFSET {offset}";

		var csb = new SqliteConnectionStringBuilder(_factory.GetConnectionString(ProjectKey, DbName))
		{
			Mode = SqliteOpenMode.ReadOnly,
		};

		try
		{
			await using var conn = new SqliteConnection(csb.ConnectionString);
			await conn.OpenAsync(ct);
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = paged;
			cmd.CommandTimeout = 30;

			await using var reader = await cmd.ExecuteReaderAsync(ct);
			var cols = new string[reader.FieldCount];
			for (var i = 0; i < reader.FieldCount; i++) cols[i] = reader.GetName(i);
			Columns = cols;

			var rows = new List<IReadOnlyList<string?>>();
			while (await reader.ReadAsync(ct))
			{
				var row = new string?[reader.FieldCount];
				for (var i = 0; i < reader.FieldCount; i++)
					row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
				rows.Add(row);
			}

			HasNext = rows.Count > PageSize;
			if (HasNext) rows.RemoveAt(rows.Count - 1);
			Rows = rows;
		}
		catch (SqliteException ex)
		{
			ErrorMessage = ex.Message;
		}
	}
}
