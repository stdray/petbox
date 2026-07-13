using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "SysAdmin")]
public sealed class WorkspacesModel : PageModel
{
	readonly IWorkspaceAdminService _workspaces;

	public WorkspacesModel(IWorkspaceAdminService workspaces) => _workspaces = workspaces;

	public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public async Task OnGetAsync() =>
		Workspaces = await _workspaces.ListAsync(HttpContext.RequestAborted);

	// The create act itself lives in WorkspaceProvisioning, reached through IWorkspaceAdminService —
	// this page and the self-service page are two doors into the same room. bypassQuota: true because
	// the page is SysAdmin-gated and a sysadmin's creates are not counted against a quota.
	public async Task<IActionResult> OnPostCreateAsync(string Key, string Name, string Description)
	{
		long? creator = long.TryParse(
			User.FindFirst(PetBoxClaims.UserId)?.Value,
			NumberStyles.Integer,
			CultureInfo.InvariantCulture,
			out var userId)
				? userId
				: null;

		var result = await _workspaces.CreateAsync(
			Key, Name, Description, creator, bypassQuota: true, HttpContext.RequestAborted);

		if (!result.Ok)
		{
			ErrorMessage = result.Error;
			await OnGetAsync();
			return Page();
		}

		this.NotifySuccess($"Workspace '{Key.Trim()}' created.");
		return RedirectToPage();
	}

	// The gate ("no user projects"), the cascade (container projects → memberships → the workspace)
	// and the $system refusal all live in IWorkspaceAdminService.DeleteAsync — the page only turns the
	// outcome into a message. That the workspace's own `$ws-<key>` memory container must NOT count as
	// a project (it made every workspace permanently undeletable) is a rule of the write, not of the
	// page that happens to render the button.
	public async Task<IActionResult> OnPostDeleteAsync(string key)
	{
		var result = await _workspaces.DeleteAsync(key, HttpContext.RequestAborted);

		if (result is WorkspaceChangeResult.Refused refused)
		{
			ErrorMessage = refused.Reason;
			await OnGetAsync();
			return Page();
		}

		this.NotifySuccess($"Workspace '{key}' deleted.");
		return RedirectToPage();
	}
}
