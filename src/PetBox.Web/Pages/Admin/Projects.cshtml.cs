using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectsModel : PageModel
{
	readonly ICoreDbFactory _f;

	public ProjectsModel(ICoreDbFactory f) => _f = f;

	// authz-bypass-project-create: bound ONLY from the route — never Form/Query — so a POST
	// body field named "WorkspaceKey" cannot retarget the write after the WorkspaceAdmin policy
	// has already checked the ROUTE workspace. ASP.NET's default composite provider order is
	// Form -> Route -> Query, which is exactly the hole [FromRoute] closes.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	public IReadOnlyList<Project> ProjectsInWorkspace { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet()
	{
		using var db = _f.Open();
		ProjectsInWorkspace = db.Projects
			.Where(p => p.WorkspaceKey == WorkspaceKey)
			.OrderBy(p => p.Key)
			.ToList();
	}

	static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		"logs", "traces", "config", "admin", "projects", "sys", "tasks", "data", "settings",
	};

	public async Task<IActionResult> OnPostCreateAsync(string key, string name, string description)
	{
		using var db = _f.Open();
		if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
		{
			ErrorMessage = "Key and Name are required.";
			OnGet();
			return Page();
		}

		if (string.Equals(WorkspaceKey, "$system", StringComparison.Ordinal))
		{
			ErrorMessage = "Cannot create projects in $system. It hosts PetBox-internal services only.";
			OnGet();
			return Page();
		}

		if (ReservedKeys.Contains(key))
		{
			ErrorMessage = $"Project key '{key}' is reserved (collides with a URL segment).";
			OnGet();
			return Page();
		}

		if (await db.Projects.AnyAsync(p => p.Key == key))
		{
			ErrorMessage = $"Project '{key}' already exists.";
			OnGet();
			return Page();
		}

		await db.InsertAsync(new Project
		{
			Key = key,
			WorkspaceKey = WorkspaceKey,
			Name = name,
			Description = description ?? string.Empty,
		});

		// Stay in the admin zone after creating a project (was bouncing to the /ui
		// project dashboard / log view).
		return Redirect(Routes.ProjectSettings(WorkspaceKey, key));
	}
}
