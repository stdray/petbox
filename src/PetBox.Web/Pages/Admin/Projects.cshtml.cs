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
	readonly PetBoxDb _db;

	public ProjectsModel(PetBoxDb db) => _db = db;

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	public IReadOnlyList<Project> ProjectsInWorkspace { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet() =>
		ProjectsInWorkspace = _db.Projects
			.Where(p => p.WorkspaceKey == WorkspaceKey)
			.OrderBy(p => p.Key)
			.ToList();

	static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		"logs", "traces", "config", "admin", "projects", "sys", "tasks", "data", "settings",
	};

	public async Task<IActionResult> OnPostCreateAsync(string key, string name, string description)
	{
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

		if (await _db.Projects.AnyAsync(p => p.Key == key))
		{
			ErrorMessage = $"Project '{key}' already exists.";
			OnGet();
			return Page();
		}

		await _db.InsertAsync(new Project
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
