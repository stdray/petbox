using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI table view (/ui/{ws}/{project}/databases/{dbName}/{tableName}).
// A read-only SQL bar + paged results. The connection is opened ReadOnly so the
// bar can't mutate data — writes/DDL go through the API (/exec) or admin schema
// apply. One SQLite open per request — a leaf page, allowed per the perf invariants.
[Authorize]
public sealed class TableModel : PageModel
{
	const int PageSize = 50;

	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly IDataDbFactory _factory;

	public TableModel(PetBoxDb db, FeatureFlags features, IDataDbFactory factory)
	{
		_db = db;
		_features = features;
		_factory = factory;
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

	public async Task<IActionResult> OnGetAsync(string? sql, int? page, CancellationToken ct)
	{
		if (!DataEnabled) return Page();

		var exists = await _db.DataDbs.AnyAsync(
			d => d.ProjectKey == ProjectKey && d.Name == DbName, ct);
		if (!exists) { DbNotFound = true; return Page(); }

		Sql = string.IsNullOrWhiteSpace(sql) ? DefaultSql : sql.Trim();
		PageNum = page is > 0 ? page.Value : 0;

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
