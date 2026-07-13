using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class WorkspaceSettingsModel : PageModel
{
	readonly IWorkspaceAdminService _workspaces;

	public WorkspaceSettingsModel(IWorkspaceAdminService workspaces) => _workspaces = workspaces;

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	public Workspace? Workspace { get; private set; }
	public int ProjectCount { get; private set; }
	public int MemberCount { get; private set; }

	[BindProperty]
	public string Name { get; set; } = string.Empty;

	[BindProperty]
	public string Description { get; set; } = string.Empty;

	public string? SuccessMessage { get; set; }
	public string? ErrorMessage { get; set; }

	public async Task OnGetAsync()
	{
		await LoadAsync();
		if (Workspace is not null)
		{
			Name = Workspace.Name;
			Description = Workspace.Description ?? string.Empty;
		}
	}

	public async Task<IActionResult> OnPostSaveAsync()
	{
		await LoadAsync();
		if (Workspace is null)
		{
			ErrorMessage = "Workspace not found.";
			return Page();
		}

		var result = await _workspaces.UpdateAsync(
			WorkspaceKey, Name, Description, HttpContext.RequestAborted);

		switch (result)
		{
			case WorkspaceChangeResult.Refused refused:
				ErrorMessage = refused.Reason;
				return Page();
			case WorkspaceChangeResult.NotFound:
				ErrorMessage = "Workspace not found.";
				return Page();
		}

		SuccessMessage = "Saved.";
		await LoadAsync();
		return Page();
	}

	// includeContainers: true so ProjectCount keeps counting exactly what it counted when this page
	// read core.db itself (every Projects row of the workspace, its `$ws-` memory container included).
	// It is a display number here, NOT the delete gate — that one lives in IWorkspaceAdminService
	// .DeleteAsync and deliberately does NOT count the container.
	async Task LoadAsync()
	{
		var overview = await _workspaces.GetOverviewAsync(
			WorkspaceKey, includeContainers: true, HttpContext.RequestAborted);
		if (overview is null) return;

		Workspace = overview.Workspace;
		ProjectCount = overview.Projects.Count;
		MemberCount = overview.MemberCount;
	}
}
