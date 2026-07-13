using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceViewer")]
public sealed class WorkspaceDetailModel : PageModel
{
	readonly IWorkspaceAdminService _workspaces;

	public WorkspaceDetailModel(IWorkspaceAdminService workspaces) => _workspaces = workspaces;

	public Workspace? Workspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public int MemberCount { get; private set; }

	// includeContainers: true — this is the workspace admin's OWN project table, the one surface that
	// shows the `$ws-<key>` memory container (it is a Projects row of this workspace). Everywhere else
	// the container is not a project: the counts and the delete gate never see it.
	public async Task OnGetAsync(string key)
	{
		var overview = await _workspaces.GetOverviewAsync(
			key, includeContainers: true, HttpContext.RequestAborted);
		if (overview is null) return;

		Workspace = overview.Workspace;
		Projects = overview.Projects;
		MemberCount = overview.MemberCount;
	}
}
