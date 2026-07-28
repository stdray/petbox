using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "SysAdmin")]
// It CREATES AND ENUMERATES TENANTS — the same sentence the MCP project_* tool type is exempted on, and
// the same class. A workspace-creating verb has no workspace to be scoped to, and the list it renders is
// every workspace there is, which is the point of the page.
//
// Note what stays scoped: /ui/admin/sys/workspaces/{key} (Admin/WorkspaceDetail) is ONE workspace named
// in the route and declares it as a tenant. The exemption ends where a single tenant is named.
[TenantExempt(TenantExemption.Provisioning,
	"creates and enumerates workspaces — i.e. the tenants themselves; a tenant-creating verb has no "
	+ "tenant to be scoped to")]
public sealed class WorkspacesModel : PageModel
{
	readonly IWorkspaceAdminService _workspaces;

	public WorkspacesModel(IWorkspaceAdminService workspaces) => _workspaces = workspaces;

	public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	// ListForSysAdminAsync, NOT ListAsync: this page is "All workspaces" — the one sysadmin-only
	// place where a workspace that lost its catalog row but kept its projects must still show up
	// (see IWorkspaceAdminService.ListForSysAdminAsync). The nav sidebar keeps reading ListAsync;
	// widening what IT shows was not this fix's job.
	public async Task OnGetAsync() =>
		Workspaces = await _workspaces.ListForSysAdminAsync(HttpContext.RequestAborted);

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
