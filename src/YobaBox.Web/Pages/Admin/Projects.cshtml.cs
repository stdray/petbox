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

	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet() => Projects = _db.Projects.OrderBy(p => p.Key).ToList();

	public async Task<IActionResult> OnPostCreateAsync(string Key, string Name, string Description)
	{
		if (string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(Name))
		{
			ErrorMessage = "Key and Name are required.";
			OnGet();
			return Page();
		}

		var exists = _db.Projects.Any(p => p.Key == Key);
		if (exists)
		{
			ErrorMessage = $"Project '{Key}' already exists.";
			OnGet();
			return Page();
		}

		await _db.InsertAsync(new Project { Key = Key, Name = Name, Description = Description ?? string.Empty });
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(string key)
	{
		if (key == "$system")
		{
			ErrorMessage = "Cannot delete $system project.";
			OnGet();
			return Page();
		}

		await _db.Projects.Where(p => p.Key == key).DeleteAsync();
		return RedirectToPage();
	}
}
