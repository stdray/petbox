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

	public void OnGet() => Projects = _db.Projects.OrderBy(p => p.Key).ToList();

	public IActionResult OnGetCreate() => Partial("_CreateProject", new Project());

	public IActionResult OnGetCreateCancel() => new EmptyResult();

	public async Task<IActionResult> OnPostCreateAsync(string Key, string Name, string Description)
	{
		if (string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(Name))
			return BadRequest("Key and Name are required.");

		var exists = _db.Projects.Any(p => p.Key == Key);
		if (exists)
			return BadRequest($"Project '{Key}' already exists.");

		await _db.InsertAsync(new Project { Key = Key, Name = Name, Description = Description ?? string.Empty });
		Response.Headers["HX-Redirect"] = Url.Page("/Admin/Projects");
		return new EmptyResult();
	}

	public async Task<IActionResult> OnDeleteAsync(string key)
	{
		if (key == "$system")
			return BadRequest("Cannot delete $system project.");

		await _db.Projects.Where(p => p.Key == key).DeleteAsync();
		return new EmptyResult();
	}
}
