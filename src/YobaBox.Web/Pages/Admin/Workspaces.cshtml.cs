using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

		var exists = _db.Workspaces.Any(w => w.Key == Key);
		if (exists)
		{
			ErrorMessage = $"Workspace '{Key}' already exists.";
			OnGet();
			return Page();
		}

		await _db.InsertAsync(new Workspace { Key = Key, Name = Name, Description = Description ?? string.Empty, CreatedAt = DateTime.UtcNow });
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
