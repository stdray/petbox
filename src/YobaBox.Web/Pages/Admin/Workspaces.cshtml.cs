using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class WorkspacesModel : PageModel
{
	readonly YobaBoxDb _db;

	public WorkspacesModel(YobaBoxDb db) => _db = db;

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
		var userIdRaw = User.FindFirst(YobaBoxClaims.UserId)?.Value;
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

		await _db.Workspaces.Where(w => w.Key == key).DeleteAsync();
		return RedirectToPage();
	}
}
