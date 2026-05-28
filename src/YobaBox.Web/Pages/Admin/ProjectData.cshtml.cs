using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Features;
using YobaBox.Core.Models;
using YobaBox.Data;

namespace YobaBox.Web.Pages.Admin;

// Two-level navigation: this page lists DataDbs for a project. Detail page
// for an individual DataDb (table introspection + paste-migration) lives in
// ProjectDataDetail.cshtml.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectDataModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly FeatureFlags _features;
	readonly IDataDbFactory _factory;

	public ProjectDataModel(YobaBoxDb db, FeatureFlags features, IDataDbFactory factory)
	{
		_db = db;
		_features = features;
		_factory = factory;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public List<DataDb> DataDbs { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!_features.IsEnabled(Feature.Data))
			return NotFound();

		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);
		if (project is null) { ProjectNotFound = true; return Page(); }

		DataDbs = await _db.DataDbs
			.Where(d => d.ProjectKey == ProjectKey)
			.OrderBy(d => d.Name)
			.ToListAsync();
		return Page();
	}

	public async Task<IActionResult> OnPostCreateAsync(string name, string? description, long? maxPageCount)
	{
		if (!_features.IsEnabled(Feature.Data)) return NotFound();
		if (string.IsNullOrWhiteSpace(name))
		{
			ErrorMessage = "Name is required.";
			await OnGetAsync();
			return Page();
		}

		var exists = await _db.DataDbs.AnyAsync((DataDb d) => d.ProjectKey == ProjectKey && d.Name == name);
		if (exists)
		{
			ErrorMessage = $"DataDb '{name}' already exists.";
			await OnGetAsync();
			return Page();
		}

		var quota = maxPageCount ?? DataDbFactory.DefaultMaxPageCount;
		try
		{
			await _factory.CreateAsync(ProjectKey, name, quota);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			ErrorMessage = "Failed to create DataDb file: " + ex.Message;
			await OnGetAsync();
			return Page();
		}

		var now = DateTime.UtcNow;
		await _db.InsertAsync(new DataDb
		{
			ProjectKey = ProjectKey,
			Name = name,
			Description = description,
			MaxPageCount = quota,
			CreatedAt = now,
			UpdatedAt = now,
		});
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(string name)
	{
		if (!_features.IsEnabled(Feature.Data)) return NotFound();

		await _db.DataDbs.Where(d => d.ProjectKey == ProjectKey && d.Name == name).DeleteAsync();
		_factory.TryDelete(ProjectKey, name);
		return RedirectToPage();
	}
}
