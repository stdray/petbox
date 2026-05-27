using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class ProjectsModel : PageModel
{
	readonly YobaBoxDb _db;

	public ProjectsModel(YobaBoxDb db) => _db = db;

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
			ErrorMessage = "Cannot create projects in $system. It hosts YobaBox-internal services only.";
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

		return Redirect(Routes.Project(WorkspaceKey, key));
	}
}
