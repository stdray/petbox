using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class WorkspaceUsersModel : PageModel
{
	readonly PetBoxDb _db;

	public WorkspaceUsersModel(PetBoxDb db) => _db = db;

	public IReadOnlyList<(WorkspaceMember Member, string Username)> Members { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet(string workspaceKey)
	{
		LoadMembers(workspaceKey);
	}

	void LoadMembers(string workspaceKey)
	{
		var members = _db.WorkspaceMembers.Where(m => m.WorkspaceKey == workspaceKey).ToList();
		var userIds = members.Select(m => m.UserId).ToHashSet();
		var users = _db.Users.Where(u => userIds.Contains(u.Id)).ToList();
		var userMap = users.ToDictionary(u => u.Id, u => u.Username);
		Members = members.Select(m => (m, userMap.GetValueOrDefault(m.UserId, "?"))).ToList();
	}

	public async Task<IActionResult> OnPostAddAsync(string workspaceKey, string Username, string? Password, WorkspaceRole Role)
	{
		if (string.IsNullOrWhiteSpace(Username))
		{
			ErrorMessage = "Username is required.";
			LoadMembers(workspaceKey);
			return Page();
		}

		var existing = _db.Users.FirstOrDefault(u => u.Username == Username);
		long userId;
		if (existing is not null)
		{
			// Existing account: ignore any supplied password — never overwrite it; add membership only.
			userId = existing.Id;
		}
		else
		{
			// New account: a password is mandatory so the user is loginable (an empty PasswordHash
			// cannot authenticate — see M008_Users), matching the Admin › Users create flow.
			if (string.IsNullOrWhiteSpace(Password))
			{
				ErrorMessage = "A password is required to create a new user.";
				LoadMembers(workspaceKey);
				return Page();
			}

			var hash = AdminPasswordHasher.Hash(Password);
			var newId = await _db.InsertWithInt64IdentityAsync(new User { Username = Username, PasswordHash = hash, CreatedAt = DateTime.UtcNow });
			userId = newId;
		}

		var already = _db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == workspaceKey);
		if (!already)
			await _db.InsertAsync(new WorkspaceMember { UserId = userId, WorkspaceKey = workspaceKey, Role = Role });

		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostRemoveAsync(string workspaceKey, long userId)
	{
		await _db.WorkspaceMembers.Where(m => m.UserId == userId && m.WorkspaceKey == workspaceKey).DeleteAsync();
		return RedirectToPage();
	}
}
