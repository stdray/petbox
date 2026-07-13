using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

// The workspace admin's project table. The key rules — reserved URL segments, '$'-prefixed
// container names, no projects in $system, no duplicate key — are NOT here: they live in
// IProjectDirectory.CreateAsync, so the next page that creates a project cannot forget one of them
// (db-out-of-pages-into-services). This page maps a refusal to its error text and nothing more.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectsModel : PageModel
{
	readonly IProjectDirectory _projects;

	public ProjectsModel(IProjectDirectory projects) => _projects = projects;

	// authz-bypass-project-create: bound ONLY from the route — never Form/Query — so a POST
	// body field named "WorkspaceKey" cannot retarget the write after the WorkspaceAdmin policy
	// has already checked the ROUTE workspace. ASP.NET's default composite provider order is
	// Form -> Route -> Query, which is exactly the hole [FromRoute] closes.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	public IReadOnlyList<Project> ProjectsInWorkspace { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	// includeContainers: this is the admin's table of what actually EXISTS in the workspace, the one
	// place the $ws-* memory container is not hidden — every other listing filters it out by default.
	public async Task OnGetAsync() =>
		ProjectsInWorkspace = await _projects.ListAsync(WorkspaceKey, includeContainers: true);

	public async Task<IActionResult> OnPostCreateAsync(string key, string name, string description)
	{
		var result = await _projects.CreateAsync(WorkspaceKey, key, name, description);
		if (result is ProjectChangeResult.Refused refused)
		{
			ErrorMessage = refused.Reason;
			await OnGetAsync();
			return Page();
		}

		// Stay in the admin zone after creating a project (was bouncing to the /ui
		// project dashboard / log view).
		return Redirect(Routes.ProjectSettings(WorkspaceKey, key));
	}
}
