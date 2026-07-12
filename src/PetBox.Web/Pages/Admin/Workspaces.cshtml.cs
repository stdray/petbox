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
	readonly ICoreDbFactory _f;

	public WorkspacesModel(ICoreDbFactory f) => _f = f;

	public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet()
	{
		using var db = _f.Open();
		Workspaces = db.Workspaces.OrderBy(w => w.Key).ToList();
	}

	public async Task<IActionResult> OnPostCreateAsync(string Key, string Name, string Description)
	{
		using var db = _f.Open();
		if (string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(Name))
		{
			ErrorMessage = "Key and Name are required.";
			OnGet();
			return Page();
		}

		// Allowlist before insert: keys become URL segments and $ws-{key} file paths.
		// Do NOT put this throw on the layout render path — gate creation only.
		if (!WorkspaceMemory.IsCreatableWorkspaceKey(Key))
		{
			ErrorMessage = "Workspace key must match ^[a-z0-9][a-z0-9-]*$ (lowercase letters, digits, hyphens; 'sys' is reserved).";
			OnGet();
			return Page();
		}

		var exists = db.Workspaces.Any(w => w.Key == Key);
		if (exists)
		{
			ErrorMessage = $"Workspace '{Key}' already exists.";
			OnGet();
			return Page();
		}

		await db.InsertAsync(new Workspace
		{
			Key = Key,
			Name = Name,
			Description = Description ?? string.Empty,
			CreatedAt = DateTime.UtcNow,
		});

		// Provision the workspace memory container so Shared-memory nav works immediately
		// (without waiting for the first MCP write or dashboard ensure).
		await WorkspaceMemory.EnsureContainerAsync(db, Key);

		// Auto-add the creator as Admin so they can switch into the workspace immediately.
		var userIdRaw = User.FindFirst(PetBoxClaims.UserId)?.Value;
		if (long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
		{
			var alreadyMember = db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == Key);
			if (!alreadyMember)
			{
				await db.InsertAsync(new WorkspaceMember
				{
					UserId = userId,
					WorkspaceKey = Key,
					Role = WorkspaceRole.Admin,
				});
			}
		}

		this.NotifySuccess($"Workspace '{Key}' created.");
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(string key)
	{
		using var db = _f.Open();
		if (key == "$system")
		{
			ErrorMessage = "Cannot delete $system workspace.";
			OnGet();
			return Page();
		}

		// Forbid deleting a non-empty workspace — projects own heavy data (DBs, boards,
		// memory, logs). The operator must delete or move them first (variant A).
		var projectCount = await db.Projects.CountAsync(p => p.WorkspaceKey == key);
		if (projectCount > 0)
		{
			ErrorMessage = $"This workspace has {projectCount} project(s). Delete or move them first.";
			OnGet();
			return Page();
		}

		// The workspace is empty: drop its memberships so no orphaned WorkspaceMember rows
		// survive the workspace, then delete the workspace itself.
		await db.WorkspaceMembers.Where(m => m.WorkspaceKey == key).DeleteAsync();
		await db.Workspaces.Where(w => w.Key == key).DeleteAsync();
		this.NotifySuccess($"Workspace '{key}' deleted.");
		return RedirectToPage();
	}
}
