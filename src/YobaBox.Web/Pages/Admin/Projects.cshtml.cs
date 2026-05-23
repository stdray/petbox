using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

public sealed class ProjectsModel : PageModel
{
	readonly YobaBoxDb _db;

	public ProjectsModel(YobaBoxDb db) => _db = db;

	public IReadOnlyList<Project> Projects { get; private set; } = [];

	public void OnGet() => Projects = _db.Projects.OrderBy(p => p.Key).ToList();

	public async Task<IActionResult> OnPostCreateAsync(string Key, string Name, string Description)
	{
		if (string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(Name))
			return BadRequest("Key and Name are required.");

		await _db.InsertAsync(new Project { Key = Key, Name = Name, Description = Description ?? string.Empty });
		return RedirectToPage();
	}

	public async Task<IActionResult> OnDeleteAsync(string key)
	{
		if (key == "$system")
			return BadRequest("Cannot delete $system project.");

		await _db.Projects.Where(p => p.Key == key).DeleteAsync();
		return new EmptyResult();
	}
}
