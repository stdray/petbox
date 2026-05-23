using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

public sealed class ProjectDetailModel : PageModel
{
	readonly YobaBoxDb _db;

	public ProjectDetailModel(YobaBoxDb db) => _db = db;

	[FromRoute]
	public string Key { get; set; } = string.Empty;

	public Project? Project { get; private set; }
	public IReadOnlyList<Service> Services { get; private set; } = [];
	public IReadOnlyList<ApiKey> Keys { get; private set; } = [];

	public void OnGet()
	{
		Project = _db.Projects.FirstOrDefault(p => p.Key == Key);
		if (Project is null) return;

		Services = _db.Services.Where(s => s.ProjectKey == Key).OrderBy(s => s.Key).ToList();
		Keys = _db.ApiKeys.Where(k => k.ProjectKey == Key).OrderByDescending(k => k.CreatedAt).ToList();
	}

	public async Task<IActionResult> OnPostCreateServiceAsync(string Key, ServiceKind Kind, string? Url)
	{
		await _db.InsertAsync(new Service
		{
			Key = Key,
			ProjectKey = this.Key,
			Kind = Kind,
			Url = Url,
			Health = ServiceHealth.Unknown,
		});
		return RedirectToPage(new { key = this.Key });
	}

	public async Task<IActionResult> OnDeleteServiceAsync(string serviceKey)
	{
		await _db.Services.Where(s => s.Key == serviceKey && s.ProjectKey == Key).DeleteAsync();
		return new EmptyResult();
	}

	public async Task<IActionResult> OnPostCreateKeyAsync(string Scopes)
	{
		var keyValue = $"yb_key_{Guid.NewGuid():N}";
		await _db.InsertAsync(new ApiKey
		{
			Key = keyValue,
			ProjectKey = Key,
			Scopes = Scopes,
			CreatedAt = DateTime.UtcNow,
		});

		TempData["NewKey"] = keyValue;
		return RedirectToPage(new { key = Key });
	}

	public async Task<IActionResult> OnDeleteRevokeKeyAsync(string keyValue)
	{
		await _db.ApiKeys.Where(k => k.Key == keyValue && k.ProjectKey == Key).DeleteAsync();
		return new EmptyResult();
	}
}
