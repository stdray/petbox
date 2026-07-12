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
	readonly ICoreDbFactory _f;

	public WorkspaceUsersModel(ICoreDbFactory f) => _f = f;

	public IReadOnlyList<(WorkspaceMember Member, string Username)> Members { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet([FromRoute(Name = "workspaceKey")] string workspaceKey)
	{
		LoadMembers(workspaceKey);
	}

	void LoadMembers(string workspaceKey)
	{
		using var db = _f.Open();
		var members = db.WorkspaceMembers.Where(m => m.WorkspaceKey == workspaceKey).ToList();
		var userIds = members.Select(m => m.UserId).ToHashSet();
		var users = db.Users.Where(u => userIds.Contains(u.Id)).ToList();
		var userMap = users.ToDictionary(u => u.Id, u => u.Username);
		Members = members.Select(m => (m, userMap.GetValueOrDefault(m.UserId, "?"))).ToList();
	}

	// authz-bypass-project-create: [FromRoute] pins this to the ROUTE workspace — never a
	// form-supplied "workspaceKey" field, which the default composite provider (Form -> Route ->
	// Query) would otherwise let override the route after the WorkspaceAdmin policy check passed.
	public async Task<IActionResult> OnPostAddAsync([FromRoute(Name = "workspaceKey")] string workspaceKey, string Username, string? Password, WorkspaceRole Role)
	{
		using var db = _f.Open();
		if (string.IsNullOrWhiteSpace(Username))
		{
			ErrorMessage = "Username is required.";
			LoadMembers(workspaceKey);
			return Page();
		}

		var existing = db.Users.FirstOrDefault(u => u.Username == Username);
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
			var newId = await db.InsertWithInt64IdentityAsync(new User { Username = Username, PasswordHash = hash, CreatedAt = DateTime.UtcNow });
			userId = newId;
		}

		var already = db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == workspaceKey);
		if (!already)
			await db.InsertAsync(new WorkspaceMember { UserId = userId, WorkspaceKey = workspaceKey, Role = Role });

		this.NotifySuccess("Member added.");
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostRemoveAsync([FromRoute(Name = "workspaceKey")] string workspaceKey, long userId)
	{
		using var db = _f.Open();
		await db.WorkspaceMembers.Where(m => m.UserId == userId && m.WorkspaceKey == workspaceKey).DeleteAsync();
		this.NotifySuccess("Member removed.");
		return RedirectToPage();
	}
}
