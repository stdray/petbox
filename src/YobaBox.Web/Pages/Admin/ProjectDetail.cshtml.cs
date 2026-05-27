using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Features;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class ProjectDetailModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly FeatureFlags _features;

	public ProjectDetailModel(YobaBoxDb db, FeatureFlags features)
	{
		_db = db;
		_features = features;
	}

	public bool DataEnabled => _features.IsEnabled("Data");

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	// Back-compat alias for the old testid/template that referenced `Model.Key`.
	public string Key => ProjectKey;

	public Project? Project { get; private set; }
	public IReadOnlyList<Service> Services { get; private set; } = [];
	public IReadOnlyList<ApiKey> Keys { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public string? NewKey { get; set; }

	public void OnGet()
	{
		Project = _db.Projects.FirstOrDefault(p => p.Key == ProjectKey);
		if (Project is null) return;

		Services = _db.Services.Where(s => s.ProjectKey == ProjectKey).OrderBy(s => s.Key).ToList();
		Keys = _db.ApiKeys.Where(k => k.ProjectKey == ProjectKey).OrderByDescending(k => k.CreatedAt).ToList();
	}

	RedirectResult Self() => Redirect(Routes.ProjectSettings(WorkspaceKey, ProjectKey));

	public async Task<IActionResult> OnPostCreateServiceAsync(string serviceKey, HealthModel HealthModel, string? Url)
	{
		if (string.IsNullOrWhiteSpace(serviceKey))
		{
			ErrorMessage = "Service key is required.";
			OnGet();
			return Page();
		}

		await _db.InsertAsync(new Service
		{
			Key = serviceKey,
			ProjectKey = ProjectKey,
			HealthModel = HealthModel,
			Url = Url,
			Health = ServiceHealth.Unknown,
		});
		return Self();
	}

	public async Task<IActionResult> OnPostDeleteServiceAsync(string serviceKey)
	{
		await _db.Services.Where(s => s.Key == serviceKey && s.ProjectKey == ProjectKey).DeleteAsync();
		return Self();
	}

	public async Task<IActionResult> OnPostCreateKeyAsync(string Scopes)
	{
		if (string.IsNullOrWhiteSpace(Scopes))
		{
			ErrorMessage = "Scopes are required.";
			OnGet();
			return Page();
		}

		var keyValue = $"yb_key_{Guid.NewGuid():N}";
		await _db.InsertAsync(new ApiKey
		{
			Key = keyValue,
			ProjectKey = ProjectKey,
			Scopes = Scopes,
			CreatedAt = DateTime.UtcNow,
		});

		NewKey = keyValue;
		OnGet();
		return Page();
	}

	public async Task<IActionResult> OnPostRevokeKeyAsync(string keyValue)
	{
		await _db.ApiKeys.Where(k => k.Key == keyValue && k.ProjectKey == ProjectKey).DeleteAsync();
		return Self();
	}
}
