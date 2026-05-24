using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class ProjectDataModel : PageModel
{
	readonly YobaBoxDb _db;

	public ProjectDataModel(YobaBoxDb db) => _db = db;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public List<DataTable> Tables { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	public async Task OnGetAsync()
	{
		var project = await _db.Projects
			.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);

		if (project is null)
		{
			ProjectNotFound = true;
			return;
		}

		Tables = await _db.DataTables
			.Where(t => t.ProjectKey == ProjectKey)
			.ToListAsync();
	}

	public async Task<IActionResult> OnPostCreateTableAsync(
		string TableName, string Columns, bool Read, bool Write, bool Delete)
	{
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
		await _db.DataTables
			.Where(t => t.Name == tableName && t.ProjectKey == ProjectKey)
			.DeleteAsync();

		return RedirectToPage();
	}
}
