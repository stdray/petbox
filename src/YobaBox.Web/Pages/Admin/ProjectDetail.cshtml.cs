using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using YobaBox.Core.Data;
using YobaBox.Core.Features;
using YobaBox.Core.Models;
using YobaBox.Log.Core.Retention;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class ProjectDetailModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly FeatureFlags _features;
	readonly RetentionOptions _retentionOptions;

	public ProjectDetailModel(YobaBoxDb db, FeatureFlags features, IOptions<RetentionOptions> retentionOptions)
	{
		_db = db;
		_features = features;
		_retentionOptions = retentionOptions.Value;
	}

	public bool DataEnabled => _features.IsEnabled("Data");
	public int DefaultRetainDays => string.Equals(ProjectKey, "$system", StringComparison.Ordinal)
		? _retentionOptions.SystemRetainDays
		: _retentionOptions.DefaultRetainDays;
	public int? RetentionOverrideDays { get; private set; }

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
		RetentionOverrideDays = _db.RetentionPolicies
			.Where(r => r.ProjectKey == ProjectKey)
			.Select(r => (int?)r.RetainDays)
			.FirstOrDefault();
	}

	public async Task<IActionResult> OnPostSetRetentionAsync(int retainDays)
	{
		if (retainDays < 1)
		{
			ErrorMessage = "Retain days must be ≥ 1.";
			OnGet();
			return Page();
		}

		var now = DateTime.UtcNow;
		var existing = _db.RetentionPolicies.FirstOrDefault(r => r.ProjectKey == ProjectKey);
		if (existing is null)
		{
			await _db.InsertAsync(new RetentionPolicy
			{
				ProjectKey = ProjectKey,
				RetainDays = retainDays,
				CreatedAt = now,
				UpdatedAt = now,
			});
		}
		else
		{
			await _db.UpdateAsync(existing with { RetainDays = retainDays, UpdatedAt = now });
		}
		return Self();
	}

	public async Task<IActionResult> OnPostClearRetentionAsync()
	{
		await _db.RetentionPolicies.Where(r => r.ProjectKey == ProjectKey).DeleteAsync();
		return Self();
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
