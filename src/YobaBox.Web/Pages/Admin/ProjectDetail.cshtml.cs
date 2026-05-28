using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Features;
using YobaBox.Core.Models;
using YobaBox.Core.Settings;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class ProjectDetailModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly FeatureFlags _features;
	readonly ISettingsResolver _settings;

	public ProjectDetailModel(YobaBoxDb db, FeatureFlags features, ISettingsResolver settings)
	{
		_db = db;
		_features = features;
		_settings = settings;
	}

	public bool DataEnabled => _features.IsEnabled(Feature.Data);

	// Effective retention as resolved by the cascade. Shown to the user as a hint
	// next to the per-project override field.
	public int EffectiveRetentionDays { get; private set; }
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

	public async Task OnGetAsync()
	{
		Project = _db.Projects.FirstOrDefault(p => p.Key == ProjectKey);
		if (Project is null) return;

		Services = _db.Services.Where(s => s.ProjectKey == ProjectKey).OrderBy(s => s.Key).ToList();
		Keys = _db.ApiKeys.Where(k => k.ProjectKey == ProjectKey).OrderByDescending(k => k.CreatedAt).ToList();

		// Effective LogSettings via cascade (project → workspace → system).
		var effective = await _settings.GetAsync<LogSettings>(Scope.Project, ProjectKey);
		EffectiveRetentionDays = string.Equals(ProjectKey, "$system", StringComparison.Ordinal)
			? effective.SystemRetainDays
			: effective.RetentionDays;

		// Has the project explicitly overridden its own retention?
		var overrideRow = _db.Settings.FirstOrDefault(s =>
			s.Scope == "Project" && s.ScopeKey == ProjectKey && s.Path == "log.retention.days");
		RetentionOverrideDays = overrideRow is null
			? null
			: int.TryParse(overrideRow.Value, out var d) ? d : null;
	}

	public async Task<IActionResult> OnPostSetRetentionAsync(int retainDays)
	{
		if (retainDays < 1)
		{
			ErrorMessage = "Retain days must be ≥ 1.";
			await OnGetAsync();
			return Page();
		}

		var oldSettings = await _settings.GetAsync<LogSettings>(Scope.Project, ProjectKey);
		var newSettings = oldSettings with { RetentionDays = retainDays };
		var userIdRaw = User.FindFirst(YobaBox.Core.Auth.YobaBoxClaims.UserId)?.Value;
		long? userId = long.TryParse(userIdRaw, out var id) ? id : null;
		await _settings.SetAsync(Scope.Project, ProjectKey, newSettings, oldSettings, userId);
		return Self();
	}

	public async Task<IActionResult> OnPostClearRetentionAsync()
	{
		await _settings.ResetAsync<LogSettings>(Scope.Project, ProjectKey, nameof(LogSettings.RetentionDays));
		return Self();
	}

	RedirectResult Self() => Redirect(Routes.ProjectSettings(WorkspaceKey, ProjectKey));

	public async Task<IActionResult> OnPostCreateServiceAsync(string serviceKey, HealthModel HealthModel, string? Url)
	{
		if (string.IsNullOrWhiteSpace(serviceKey))
		{
			ErrorMessage = "Service key is required.";
			await OnGetAsync();
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

	public async Task<IActionResult> OnPostCreateKeyAsync(string name, string[]? scopes)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			ErrorMessage = "Name is required.";
			await OnGetAsync();
			return Page();
		}

		var raw = scopes is null ? "" : string.Join(",", scopes);
		var (valid, invalid) = YobaBox.Core.Auth.ApiKeyScopes.Validate(raw);
		if (invalid.Count > 0)
		{
			ErrorMessage = "Unknown scope(s): " + string.Join(", ", invalid)
				+ ". Pick from the checkbox list — typed input is not supported.";
			await OnGetAsync();
			return Page();
		}
		if (valid.Count == 0)
		{
			ErrorMessage = "At least one scope is required.";
			await OnGetAsync();
			return Page();
		}

		var keyValue = $"yb_key_{Guid.NewGuid():N}";
		await _db.InsertAsync(new ApiKey
		{
			Key = keyValue,
			ProjectKey = ProjectKey,
			Scopes = string.Join(",", valid),
			Name = name.Trim(),
			CreatedAt = DateTime.UtcNow,
		});

		NewKey = keyValue;
		await OnGetAsync();
		return Page();
	}

	public async Task<IActionResult> OnPostRevokeKeyAsync(string keyValue)
	{
		await _db.ApiKeys.Where(k => k.Key == keyValue && k.ProjectKey == ProjectKey).DeleteAsync();
		return Self();
	}
}
