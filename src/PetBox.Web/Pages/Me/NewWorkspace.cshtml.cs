using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;

namespace PetBox.Web.Pages.Me;

// Self-service workspace creation (spec workspace-create-permission + workspace-creator-is-admin).
//
// The page carries the CanCreateWorkspace policy, so an account whose quota is spent (or was never
// granted) is denied the GET. That is NOT the enforcement — a hidden button is a UI courtesy. The
// gate is inside WorkspaceProvisioning.CreateAsync, which claims the account's slot with the quota
// check welded into the INSERT: two of these posts racing each other cannot both win.
[Authorize(Policy = "CanCreateWorkspace")]
public sealed class NewWorkspaceModel : PageModel
{
	readonly WorkspaceProvisioning _provisioning;

	public NewWorkspaceModel(WorkspaceProvisioning provisioning) => _provisioning = provisioning;

	public string? ErrorMessage { get; private set; }

	public void OnGet() { }

	public async Task<IActionResult> OnPostAsync(string? key, string? name, string? description)
	{
		var isSysAdmin = User.HasClaim(PetBoxClaims.IsSysAdmin, "true");
		long? creator = long.TryParse(
			User.FindFirst(PetBoxClaims.UserId)?.Value,
			NumberStyles.Integer,
			CultureInfo.InvariantCulture,
			out var userId)
				? userId
				: null;

		var result = await _provisioning.CreateAsync(
			key, name, description, creator, bypassQuota: isSysAdmin, HttpContext.RequestAborted);

		if (!result.Ok)
		{
			ErrorMessage = result.Error;
			return Page();
		}

		// Land on the ADMIN overview of the new workspace, not the read-only /ui status dashboard
		// (onboarding-first-workspace): the creator is already its Admin (the insert above) —
		// WorkspaceClaimsRefresher rebuilds the membership claims from the DB on the very next
		// request, so this redirect lands inside, with admin rights, on the same cookie — and their
		// very next move is an admin action (add a project, add a member), which the plain dashboard
		// has no path to. Bouncing a brand-new admin out to the user zone right after they finished
		// the first half of setup was the reported defect; landing in admin keeps them where the
		// second half (create a project) actually lives.
		this.NotifySuccess($"Workspace '{key!.Trim()}' created — you are its administrator.");
		return Redirect(Routes.WorkspaceAdmin(key.Trim()));
	}
}
