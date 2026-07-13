using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "SysAdmin")]
public sealed class UsersModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly AdminOptions _adminOptions;

	public UsersModel(ICoreDbFactory f, IOptions<AdminOptions> adminOptions)
	{
		_f = f;
		_adminOptions = adminOptions.Value;
	}

	public sealed record UserRow(
		long Id,
		string Username,
		DateTime CreatedAt,
		bool IsBootstrapAdmin,
		int WorkspaceQuota,
		int WorkspacesOwned,
		IReadOnlyList<MembershipRow> Memberships);
	public sealed record MembershipRow(string WorkspaceKey, WorkspaceRole Role);

	public IReadOnlyList<UserRow> Users { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet() => Load();

	void Load()
	{
		using var db = _f.Open();
		var users = db.Users.OrderBy(u => u.Username).ToList();
		var members = db.WorkspaceMembers.ToList();

		Users = [.. users.Select(u => new UserRow(
			u.Id,
			u.Username,
			u.CreatedAt,
			IsBootstrapAdmin(u.Username),
			u.WorkspaceQuota,
			// "used" is shown next to the quota so the admin sets a number against a fact, not a guess.
			// Same criterion the quota is enforced by (WorkspaceProvisioning.CountOwnedWorkspacesAsync):
			// workspaces the account is Admin of, excluding the seeded $system.
			members.Count(m => m.UserId == u.Id
				&& m.Role == WorkspaceRole.Admin
				&& m.WorkspaceKey != WorkspaceMemory.SystemWorkspace),
			[.. members
				.Where(m => m.UserId == u.Id)
				.OrderBy(m => m.WorkspaceKey)
				.Select(m => new MembershipRow(m.WorkspaceKey, m.Role))]))];
	}

	bool IsBootstrapAdmin(string username) =>
		!string.IsNullOrEmpty(_adminOptions.Username)
		&& string.Equals(username, _adminOptions.Username, StringComparison.Ordinal);

	long CurrentUserId =>
		long.TryParse(User.FindFirst(PetBoxClaims.UserId)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
			? id
			: -1;

	// `workspaceQuota` is deliberately NULLABLE and has NO default: the form field ships empty and the
	// admin must type a number (spec workspace-create-permission — the right is granted explicitly).
	// A missing value is an error, NOT a silent 0: "nobody decided" and "decided: none" are different
	// facts, and only the second one may be written to an account.
	public async Task<IActionResult> OnPostCreateAsync(string? username, string? password, int? workspaceQuota)
	{
		using var db = _f.Open();
		if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
		{
			ErrorMessage = "Username and password are required.";
			Load();
			return Page();
		}

		if (workspaceQuota is not { } quota)
		{
			ErrorMessage = "Workspace allowance is required — enter 0 if this account may not create workspaces.";
			Load();
			return Page();
		}

		if (quota < 0)
		{
			ErrorMessage = "Workspace allowance cannot be negative.";
			Load();
			return Page();
		}

		if (db.Users.Any(u => u.Username == username))
		{
			ErrorMessage = $"User '{username}' already exists.";
			Load();
			return Page();
		}

		await db.InsertWithInt64IdentityAsync(new User
		{
			Username = username.Trim(),
			PasswordHash = AdminPasswordHasher.Hash(password),
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = quota,
		});

		this.NotifySuccess($"User '{username.Trim()}' created.");
		return RedirectToPage();
	}

	// Raising or lowering an existing account's allowance. Lowering it (even to 0) does NOT touch the
	// workspaces the account already created, nor its Admin role in them — that would leave a
	// workspace with no administrator. It only governs the NEXT create.
	public async Task<IActionResult> OnPostSetQuotaAsync(long userId, int? workspaceQuota)
	{
		using var db = _f.Open();

		if (workspaceQuota is not { } quota || quota < 0)
		{
			ErrorMessage = "Workspace allowance must be a number of 0 or more.";
			Load();
			return Page();
		}

		await db.Users
			.Where(u => u.Id == userId)
			.Set(u => u.WorkspaceQuota, quota)
			.UpdateAsync();

		this.NotifySuccess("Workspace allowance updated.");
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostResetPasswordAsync(long userId, string? newPassword)
	{
		using var db = _f.Open();
		if (string.IsNullOrWhiteSpace(newPassword))
		{
			ErrorMessage = "New password is required.";
			Load();
			return Page();
		}

		await db.Users
			.Where(u => u.Id == userId)
			.Set(u => u.PasswordHash, AdminPasswordHasher.Hash(newPassword))
			.UpdateAsync();

		this.NotifySuccess("Password reset.");
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(long userId)
	{
		using var db = _f.Open();
		var user = db.Users.FirstOrDefault(u => u.Id == userId);
		if (user is null)
			return RedirectToPage();

		if (userId == CurrentUserId)
		{
			ErrorMessage = "You cannot delete your own account.";
			Load();
			return Page();
		}

		if (IsBootstrapAdmin(user.Username))
		{
			ErrorMessage = "The bootstrap admin account cannot be deleted from here.";
			Load();
			return Page();
		}

		// Guard the last sysadmin: a user is a sysadmin if they hold $system Admin.
		var isSysAdmin = db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == "$system" && m.Role == WorkspaceRole.Admin);
		if (isSysAdmin)
		{
			var sysAdminCount = db.WorkspaceMembers.Count(m => m.WorkspaceKey == "$system" && m.Role == WorkspaceRole.Admin);
			if (sysAdminCount <= 1)
			{
				ErrorMessage = "Cannot delete the last system administrator.";
				Load();
				return Page();
			}
		}

		await db.WorkspaceMembers.Where(m => m.UserId == userId).DeleteAsync();
		await db.Users.Where(u => u.Id == userId).DeleteAsync();

		this.NotifySuccess($"User '{user.Username}' deleted.");
		return RedirectToPage();
	}
}
