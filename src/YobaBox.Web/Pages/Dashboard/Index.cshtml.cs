using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Web.Navigation;

namespace YobaBox.Web.Pages.Dashboard;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly INavigationContext _nav;

	public IndexModel(YobaBoxDb db, INavigationContext nav)
	{
		_db = db;
		_nav = nav;
	}

	public string WorkspaceKey { get; private set; } = "$system";
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public Dictionary<string, IReadOnlyList<Service>> ServicesByProject { get; private set; } = new();

	public void OnGet()
	{
		WorkspaceKey = _nav.CurrentWorkspaceKey;
		var wsKey = WorkspaceKey;
		Projects = _db.Projects.Where(p => p.WorkspaceKey == wsKey).OrderBy(p => p.Key).ToList();

		var projectKeys = Projects.Select(p => p.Key).ToHashSet();
		var allServices = _db.Services.OrderBy(s => s.Key).ToList();
		ServicesByProject = allServices
			.Where(s => projectKeys.Contains(s.ProjectKey))
			.GroupBy(s => s.ProjectKey)
			.ToDictionary(g => g.Key, g => (IReadOnlyList<Service>)g.ToList());
	}
}
