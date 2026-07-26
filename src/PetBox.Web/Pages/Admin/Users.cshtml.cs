using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Admin;

// The sysadmin accounts table. It holds NO rule of its own: every guard that used to live here —
// the allowance may not be silently defaulted, an admin may not delete themselves, the bootstrap
// account and the last $system admin may not be deleted — now lives in IUserAdminService, welded to
// the writes it guards (db-out-of-pages-into-services). What is left is this page's actual job:
// turn a UserChangeResult into something a human reads.
[Authorize(Policy = "SysAdmin")]
// The accounts table of the whole installation: it creates and deletes principals and sets each one's
// workspace ALLOWANCE. That allowance is a grant that spans tenants — the account it is written on can
// then go and own workspaces — so the class is `provisioning`, not `fleet-wide`: what is handed out here
// is access, not a node.
//
// A user account belongs to no workspace (WorkspaceMembers is a separate table, edited per workspace on
// Admin/WorkspaceUsers, which DOES declare its tenant), so there is no tenant in the route and none the
// page could be narrowed to.
[TenantExempt(TenantExemption.Provisioning,
	"creates, deletes and re-authorizes the installation's user accounts and their workspace allowance; "
	+ "an account belongs to no single tenant and the grant it carries spans them")]
public sealed class UsersModel : PageModel
{
	readonly IUserAdminService _users;

	public UsersModel(IUserAdminService users) => _users = users;

	public IReadOnlyList<UserAccount> Users { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public async Task OnGetAsync() => await LoadAsync();

	async Task LoadAsync() => Users = await _users.ListAsync();

	long CurrentUserId =>
		long.TryParse(User.FindFirst(PetBoxClaims.UserId)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
			? id
			: -1;

	// The allowance field ships EMPTY and is required — the service refuses a missing value rather
	// than writing a silent 0 (spec workspace-create-permission). The page only shows the refusal.
	public async Task<IActionResult> OnPostCreateAsync(string? username, string? password, int? workspaceQuota)
	{
		var result = await _users.CreateAsync(username, password, workspaceQuota);
		if (result is UserChangeResult.Refused refused)
			return await RefuseAsync(refused.Reason);

		this.NotifySuccess($"User '{username!.Trim()}' created.");
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostSetQuotaAsync(long userId, int? workspaceQuota) =>
		await ApplyAsync(await _users.SetQuotaAsync(userId, workspaceQuota), "Workspace allowance updated.");

	public async Task<IActionResult> OnPostResetPasswordAsync(long userId, string? newPassword) =>
		await ApplyAsync(await _users.ResetPasswordAsync(userId, newPassword), "Password reset.");

	public async Task<IActionResult> OnPostDeleteAsync(long userId)
	{
		// The name is read BEFORE the delete purely so the success notice can say who went; a row
		// that is already gone is not an error (the table just re-renders without it), which is the
		// behaviour a double-submit of the delete button relies on.
		var account = await _users.GetAsync(userId);
		if (account is null) return RedirectToPage();

		return await ApplyAsync(
			await _users.DeleteAsync(userId, CurrentUserId), $"User '{account.Username}' deleted.");
	}

	async Task<IActionResult> ApplyAsync(UserChangeResult result, string success)
	{
		switch (result)
		{
			case UserChangeResult.Refused refused:
				return await RefuseAsync(refused.Reason);
			case UserChangeResult.NotFound:
				// Nothing to report and nothing to fix: the account is not there, so the fresh table
				// is the answer.
				return RedirectToPage();
			default:
				this.NotifySuccess(success);
				return RedirectToPage();
		}
	}

	// A refusal is shown ON the table (not carried across a redirect): the admin sees why, with the
	// form they submitted still in front of them.
	async Task<IActionResult> RefuseAsync(string reason)
	{
		ErrorMessage = reason;
		await LoadAsync();
		return Page();
	}
}
