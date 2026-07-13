using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Me;

[Authorize]
public sealed class SecurityModel : PageModel
{
	// IAccountSelfService, NOT IUserAdminService: EVERY logged-in user reaches this page, and the
	// admin service can reset ANY account's password. This door takes no user id and no username —
	// the account is read off the authenticated principal inside the service, so no form field on
	// this page can ever name somebody else's row.
	readonly IAccountSelfService _accounts;

	public SecurityModel(IAccountSelfService accounts) => _accounts = accounts;

	public string? SuccessMessage { get; set; }
	public string? ErrorMessage { get; set; }

	public void OnGet() { }

	public async Task<IActionResult> OnPostChangePasswordAsync(
		string? currentPassword, string? newPassword, string? confirmPassword)
	{
		// The confirmation field is a FORM concern (it never reaches the database and there is
		// nothing to authorize about it), so it stays here. Everything that touches the account —
		// which account, its current password, the strength rule, the write — is the service's.
		if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(confirmPassword))
		{
			ErrorMessage = "All three fields are required.";
			return Page();
		}
		if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
		{
			ErrorMessage = "New password and confirmation do not match.";
			return Page();
		}

		// `User` is the authenticated principal — the ONLY identity this page can hand over.
		var result = await _accounts.ChangeOwnPasswordAsync(
			User, currentPassword, newPassword, HttpContext.RequestAborted);

		if (result is PasswordChangeResult.Refused refused)
		{
			ErrorMessage = refused.Reason;
			return Page();
		}

		SuccessMessage = "Password updated.";
		return Page();
	}
}
