using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "SysAdmin")]
public sealed class WorkspacesModel : PageModel
{
	readonly PetBoxDb _db;

	public WorkspacesModel(PetBoxDb db) => _db = db;

	public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet() => Workspaces = _db.Workspaces.OrderBy(w => w.Key).ToList();

	public async Task<IActionResult> OnPostCreateAsync(string Key, string Name, string Description)
	{
		if (string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(Name))
		{
			ErrorMessage = "Key and Name are required.";
			OnGet();
			return Page();
		}

		if (string.Equals(Key, "sys", StringComparison.OrdinalIgnoreCase))
		{
			ErrorMessage = "Workspace key 'sys' is reserved.";
			OnGet();
			return Page();
		}

		var exists = _db.Workspaces.Any(w => w.Key == Key);
		if (exists)
		{
			ErrorMessage = $"Workspace '{Key}' already exists.";
			OnGet();
			return Page();
		}

		await _db.InsertAsync(new Workspace
		{
			Key = Key,
			Name = Name,
			Description = Description ?? string.Empty,
			CreatedAt = DateTime.UtcNow,
		});

		// Auto-add the creator as Admin so they can switch into the workspace immediately.
		var userIdRaw = User.FindFirst(PetBoxClaims.UserId)?.Value;
		if (long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
		{
			var alreadyMember = _db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == Key);
			if (!alreadyMember)
			{
				await _db.InsertAsync(new WorkspaceMember
				{
					UserId = userId,
					WorkspaceKey = Key,
					Role = WorkspaceRole.Admin,
				});
			}
		}

		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(string key)
	{
		if (key == "$system")
		{
			ErrorMessage = "Cannot delete $system workspace.";
			OnGet();
			return Page();
		}

		// Forbid deleting a non-empty workspace — projects own heavy data (DBs, boards,
		// memory, logs). The operator must delete or move them first (variant A).
		var projectCount = await _db.Projects.CountAsync(p => p.WorkspaceKey == key);
		if (projectCount > 0)
		{
			ErrorMessage = $"This workspace has {projectCount} project(s). Delete or move them first.";
			OnGet();
			return Page();
		}

		// The workspace is empty: drop its memberships so no orphaned WorkspaceMember rows
		// survive the workspace, then delete the workspace itself.
		await _db.WorkspaceMembers.Where(m => m.WorkspaceKey == key).DeleteAsync();
		await _db.Workspaces.Where(w => w.Key == key).DeleteAsync();
		return RedirectToPage();
	}
}
