using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Features;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class ProjectDataModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly FeatureFlags _features;

	public ProjectDataModel(YobaBoxDb db, FeatureFlags features)
	{
		_db = db;
		_features = features;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public List<DataTable> Tables { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!_features.IsEnabled("Data"))
			return NotFound();

		var project = await _db.Projects
			.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);

		if (project is null)
		{
			ProjectNotFound = true;
			return Page();
		}

		Tables = await _db.DataTables
			.Where(t => t.ProjectKey == ProjectKey)
			.ToListAsync();

		return Page();
	}

	public async Task<IActionResult> OnPostCreateTableAsync(
		string TableName, string Columns, bool Read, bool Write, bool Delete)
	{
		if (!_features.IsEnabled("Data"))
			return NotFound();

		if (string.IsNullOrWhiteSpace(TableName))
		{
			ErrorMessage = "Table name is required.";
			return Page();
		}

		var existing = await _db.DataTables
			.FirstOrDefaultAsync((DataTable t) => t.Name == TableName);

		if (existing is not null)
		{
			ErrorMessage = "Table already exists.";
			return Page();
		}

		await _db.InsertAsync(new DataTable
		{
			Name = TableName,
			ProjectKey = ProjectKey,
			Columns = Columns,
			Read = Read,
			Write = Write,
			Delete = Delete,
			Created = true,
		});

		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteTableAsync(string tableName)
	{
		if (!_features.IsEnabled("Data"))
			return NotFound();

		await _db.DataTables
			.Where(t => t.Name == tableName && t.ProjectKey == ProjectKey)
			.DeleteAsync();

		return RedirectToPage();
	}
}
