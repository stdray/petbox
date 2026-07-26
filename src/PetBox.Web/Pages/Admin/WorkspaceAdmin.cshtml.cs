using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Core.Auth;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceMember")]
// {workspaceKey} in the route IS the target tenant. ITenantAuthorizer answers both credential
// kinds from that one value — a session on membership of that workspace, an api key on "a project
// claim authorizes its workspace" — so this page needs no check of its own. The MinRole the policy
// above demands is a DIFFERENT axis and stays there.
[TenantFrom(TenantSource.Route, "workspaceKey", tenant: TenantKind.Workspace)]
public sealed class WorkspaceAdminModel : PageModel
{
	readonly IWorkspaceAdminService _workspaces;
	readonly IProjectDirectory _projects;
	readonly IWorkspaceMembershipService _members;
	readonly IConfigDirectory _config;

	public WorkspaceAdminModel(
		IWorkspaceAdminService workspaces,
		IProjectDirectory projects,
		IWorkspaceMembershipService members,
		IConfigDirectory config)
	{
		_workspaces = workspaces;
		_projects = projects;
		_members = members;
		_config = config;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	public Workspace? Workspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public int MemberCount { get; private set; }
	public int ProjectCount { get; private set; }
	public int BindingCount { get; private set; }

	public async Task OnGetAsync()
	{
		Workspace = await _workspaces.GetAsync(WorkspaceKey);
		if (Workspace is null) return;

		// includeContainers: true — this overview table is the one surface that DOES show the
		// workspace's own $ws-* memory container alongside its user projects (see IProjectDirectory's
		// doc comment); the original inline query never filtered it either.
		Projects = await _projects.ListAsync(WorkspaceKey, includeContainers: true);
		ProjectCount = Projects.Count;
		MemberCount = await _members.CountMembersAsync(WorkspaceKey);

		BindingCount = await _config.CountActiveBindingsAsync(WorkspaceKey);
	}
}
