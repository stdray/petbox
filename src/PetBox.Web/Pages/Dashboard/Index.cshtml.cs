using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages.Dashboard;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly PetBoxDb _db;
	readonly INavigationContext _nav;

	public IndexModel(PetBoxDb db, INavigationContext nav)
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
