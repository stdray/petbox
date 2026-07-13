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

		var ct = HttpContext.RequestAborted;
		var projects = await db.Projects
			.Where(p => p.WorkspaceKey == key)
			.Select(p => p.Key)
			.ToListAsync(ct);

		// "Empty" means no USER projects. A workspace's own `$ws-<key>` memory container is a
		// Projects row too — provisioned with the workspace itself (WorkspaceMemory
		// .EnsureContainerAsync, spec reserved-workspace-project) — and counting it here meant a
		// freshly created, entirely empty workspace already reported "1 project(s)" and could never
		// be deleted by anyone, sysadmin included, since the container has no delete button of its
		// own. So the gate asks about projects a human made, and the container is not one.
		var userProjects = projects.Where(p => !WorkspaceMemory.IsWorkspaceContainer(p)).ToList();
		if (userProjects.Count > 0)
		{
			// Forbid deleting a non-empty workspace — projects own heavy data (DBs, boards,
			// memory, logs). The operator must delete or move them first (variant A).
			ErrorMessage = $"This workspace has {userProjects.Count} project(s). Delete or move them first.";
			OnGet();
			return Page();
		}

		// The workspace is empty of user projects: its container is the workspace's own belonging,
		// so it dies WITH it rather than blocking it — full cascade (ProjectDeletion), so the
		// container's memory stores/boards/keys go too and its files are reclaimed by the orphan
		// sweepers. ProjectDeletion.IsReserved would refuse this key; that guard protects a
		// container whose workspace still LIVES, which is exactly what is ending here.
		foreach (var container in projects)
			await ProjectDeletion.DeleteAsync(db, container, ct);

		// Drop the memberships so no orphaned WorkspaceMember rows survive the workspace (they also
		// ARE the owner's quota ledger — leaving them would make an allowance a one-shot ticket),
		// then delete the workspace itself.
		await db.WorkspaceMembers.Where(m => m.WorkspaceKey == key).DeleteAsync(ct);
		await db.Workspaces.Where(w => w.Key == key).DeleteAsync(ct);
		this.NotifySuccess($"Workspace '{key}' deleted.");
		return RedirectToPage();
	}
}
