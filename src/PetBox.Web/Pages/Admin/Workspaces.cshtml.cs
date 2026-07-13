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
	readonly WorkspaceProvisioning _provisioning;

	public WorkspacesModel(ICoreDbFactory f, WorkspaceProvisioning provisioning)
	{
		_f = f;
		_provisioning = provisioning;
	}

	public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet()
	{
		using var db = _f.Open();
		Workspaces = db.Workspaces.OrderBy(w => w.Key).ToList();
	}

	// The create act itself lives in WorkspaceProvisioning — this page and the self-service page are
	// two doors into the same room. bypassQuota: true because the page is SysAdmin-gated and a
	// sysadmin's creates are not counted against a quota.
	public async Task<IActionResult> OnPostCreateAsync(string Key, string Name, string Description)
	{
		long? creator = long.TryParse(
			User.FindFirst(PetBoxClaims.UserId)?.Value,
			NumberStyles.Integer,
			CultureInfo.InvariantCulture,
			out var userId)
				? userId
				: null;

		var result = await _provisioning.CreateAsync(
			Key, Name, Description, creator, bypassQuota: true, HttpContext.RequestAborted);

		if (!result.Ok)
		{
			ErrorMessage = result.Error;
			OnGet();
			return Page();
		}

		this.NotifySuccess($"Workspace '{Key.Trim()}' created.");
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
