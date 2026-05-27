using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;

namespace YobaBox.Web.Pages.Me;

[Authorize]
public sealed class SecurityModel : PageModel
{
	readonly YobaBoxDb _db;

	public SecurityModel(YobaBoxDb db) => _db = db;

	public string? SuccessMessage { get; set; }
	public string? ErrorMessage { get; set; }

	public void OnGet() { }

	public async Task<IActionResult> OnPostChangePasswordAsync(string? currentPassword, string? newPassword, string? confirmPassword)
	{
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
		if (newPassword.Length < 8)
		{
			ErrorMessage = "New password must be at least 8 characters.";
			return Page();
		}

		var userIdRaw = User.FindFirst(YobaBoxClaims.UserId)?.Value;
		if (!long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
		{
			ErrorMessage = "Session is missing user id. Sign out and back in.";
			return Page();
		}

		var user = _db.Users.FirstOrDefault(u => u.Id == userId);
		if (user is null)
		{
			ErrorMessage = "User not found.";
			return Page();
		}

		if (!AdminPasswordHasher.Verify(currentPassword, user.PasswordHash))
		{
			ErrorMessage = "Current password is incorrect.";
			return Page();
		}

		var newHash = AdminPasswordHasher.Hash(newPassword);
		await _db.Users.Where(u => u.Id == userId).Set(u => u.PasswordHash, newHash).UpdateAsync();

		SuccessMessage = "Password updated.";
		return Page();
	}
}
