using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class WorkspaceSettingsModel : PageModel
{
	readonly ICoreDbFactory _f;

	public WorkspaceSettingsModel(ICoreDbFactory f) => _f = f;

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	public Workspace? Workspace { get; private set; }
	public int ProjectCount { get; private set; }
	public int MemberCount { get; private set; }

	[BindProperty]
	public string Name { get; set; } = string.Empty;

	[BindProperty]
	public string Description { get; set; } = string.Empty;

	public string? SuccessMessage { get; set; }
	public string? ErrorMessage { get; set; }

	public void OnGet()
	{
		Load();
		if (Workspace is not null)
		{
			Name = Workspace.Name;
			Description = Workspace.Description ?? string.Empty;
		}
	}

	public async Task<IActionResult> OnPostSaveAsync()
	{
		using var db = _f.Open();
		Load();
		if (Workspace is null)
		{
			ErrorMessage = "Workspace not found.";
			return Page();
		}

		if (string.IsNullOrWhiteSpace(Name))
		{
			ErrorMessage = "Name is required.";
			return Page();
		}

		await db.Workspaces
			.Where(w => w.Key == WorkspaceKey)
			.Set(w => w.Name, Name)
			.Set(w => w.Description, Description ?? string.Empty)
			.UpdateAsync();

		SuccessMessage = "Saved.";
		Load();
		return Page();
	}

	void Load()
	{
		using var db = _f.Open();
		Workspace = db.Workspaces.FirstOrDefault(w => w.Key == WorkspaceKey);
		if (Workspace is null) return;
		ProjectCount = db.Projects.Count(p => p.WorkspaceKey == WorkspaceKey);
		MemberCount = db.WorkspaceMembers.Count(m => m.WorkspaceKey == WorkspaceKey);
	}
}
