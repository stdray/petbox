using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Dashboard;

public sealed class ProjectModel : PageModel
{
	readonly YobaBoxDb _db;

	public ProjectModel(YobaBoxDb db) => _db = db;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public string ProjectName { get; private set; } = string.Empty;
	public List<Service> Services { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }

	public async Task OnGetAsync()
	{
		var project = await _db.Projects
			.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);

		if (project is null)
		{
			ProjectNotFound = true;
			return;
		}

		ProjectName = project.Name;

		Services = await _db.Services
			.Where(s => s.ProjectKey == ProjectKey)
			.ToListAsync();
	}
}
