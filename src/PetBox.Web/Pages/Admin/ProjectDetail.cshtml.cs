using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Web.Pages.Admin;

[Authorize]
public sealed class ProjectDetailModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly ISettingsResolver _settings;

	public ProjectDetailModel(PetBoxDb db, FeatureFlags features, ISettingsResolver settings)
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

	// The project's effective commit-view URL template (RepoSettings, cascaded). Empty = unset →
	// commit refs/hashes render as plain text. A literal {sha} placeholder is expanded per commit.
	public string CommitUrlTemplate { get; private set; } = "";

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	// Back-compat alias for the old testid/template that referenced `Model.Key`.
	public string Key => ProjectKey;

	public Project? Project { get; private set; }
	public IReadOnlyList<HealthEndpoint> HealthEndpoints { get; private set; } = [];
	public IReadOnlyList<ApiKey> Keys { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public string? NewKey { get; set; }

	public async Task OnGetAsync()
	{
		Project = _db.Projects.FirstOrDefault(p => p.Key == ProjectKey);
		if (Project is null) return;

		HealthEndpoints = _db.HealthEndpoints.Where(e => e.ProjectKey == ProjectKey).OrderBy(e => e.Url).ToList();
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

		// Effective commit-view template via the same cascade (project → workspace → system).
		CommitUrlTemplate = (await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey)).CommitUrlTemplate;
	}

	// Set (or clear, when blank) the per-project commit-view URL template. An empty text input
	// binds to null, so normalize to "" before comparing/writing — a blank submit resets the
	// override so the project falls back up the cascade.
	public async Task<IActionResult> OnPostSetCommitTemplateAsync(string? commitUrlTemplate)
	{
		var trimmed = (commitUrlTemplate ?? string.Empty).Trim();
		var oldSettings = await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey);

		if (trimmed.Length == 0)
		{
			await _settings.ResetAsync<RepoSettings>(Scope.Project, ProjectKey, nameof(RepoSettings.CommitUrlTemplate));
			return Self();
		}

		var newSettings = oldSettings with { CommitUrlTemplate = trimmed };
		var userIdRaw = User.FindFirst(PetBox.Core.Auth.PetBoxClaims.UserId)?.Value;
		long? userId = long.TryParse(userIdRaw, out var uid) ? uid : null;
		await _settings.SetAsync(Scope.Project, ProjectKey, newSettings, oldSettings, userId);
		return Self();
	}

	// Drop the project's own commit-view override so it falls back up the cascade.
	public async Task<IActionResult> OnPostClearCommitTemplateAsync()
	{
		await _settings.ResetAsync<RepoSettings>(Scope.Project, ProjectKey, nameof(RepoSettings.CommitUrlTemplate));
		return Self();
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
		var userIdRaw = User.FindFirst(PetBox.Core.Auth.PetBoxClaims.UserId)?.Value;
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

	public async Task<IActionResult> OnPostCreateHealthEndpointAsync(string url, int? intervalSeconds)
	{
		if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
		{
			ErrorMessage = "A valid absolute URL is required.";
			await OnGetAsync();
			return Page();
		}

		await _db.InsertAsync(new HealthEndpoint
		{
			ProjectKey = ProjectKey,
			Url = url.Trim(),
			Enabled = true,
			IntervalSeconds = intervalSeconds is { } s && s >= 5 ? s : 60,
			CreatedAt = DateTime.UtcNow,
			CreatedBy = User.Identity?.Name,
		});
		return Self();
	}

	public async Task<IActionResult> OnPostDeleteHealthEndpointAsync(long id)
	{
		await _db.HealthEndpoints.Where(e => e.Id == id && e.ProjectKey == ProjectKey).DeleteAsync();
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
		var (valid, invalid) = PetBox.Core.Auth.ApiKeyScopes.Validate(raw);
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

	// Edit the scopes of an existing key in place (scopes were previously fixed at
	// mint time — finding D5). Same validation as minting: known scopes, at least one.
	public async Task<IActionResult> OnPostUpdateKeyScopesAsync(string keyValue, string[]? scopes)
	{
		var raw = scopes is null ? "" : string.Join(",", scopes);
		var (valid, invalid) = PetBox.Core.Auth.ApiKeyScopes.Validate(raw);
		if (invalid.Count > 0)
		{
			ErrorMessage = "Unknown scope(s): " + string.Join(", ", invalid);
			await OnGetAsync();
			return Page();
		}
		if (valid.Count == 0)
		{
			ErrorMessage = "At least one scope is required.";
			await OnGetAsync();
			return Page();
		}

		await _db.ApiKeys
			.Where(k => k.Key == keyValue && k.ProjectKey == ProjectKey)
			.Set(k => k.Scopes, string.Join(",", valid))
			.UpdateAsync();
		return Self();
	}
}
