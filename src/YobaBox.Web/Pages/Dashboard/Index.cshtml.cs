using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Dashboard;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly YobaBoxDb _db;

	public IndexModel(YobaBoxDb db) => _db = db;

	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public Dictionary<string, IReadOnlyList<Service>> ServicesByProject { get; private set; } = new();

	public void OnGet()
	{
		Projects = _db.Projects.OrderBy(p => p.Key).ToList();
		var allServices = _db.Services.OrderBy(s => s.Key).ToList();
		ServicesByProject = allServices.GroupBy(s => s.ProjectKey).ToDictionary(g => g.Key, g => (IReadOnlyList<Service>)g.ToList());
	}
}
