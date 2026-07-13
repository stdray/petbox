using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Core.Health;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

// The project settings page. It holds NO db factory and opens NO connection: every read and write
// goes through a service (AGENTS.md, "the database is visible only in the service layer"; work
// `db-out-of-pages-into-services'). The four surfaces it needs, and who owns each:
//
//   projects          -> IProjectDirectory
//   api keys          -> AgentKeyAdminService      (project-confined: ListByProject/Mint/Revoke/SetScopes)
//   health endpoints  -> IHealthEndpointDirectory  (the pull-mode source list)
//   settings          -> ISettingsResolver (the cascade) + ISettingsStore (is there an OVERRIDE row?)
//
// The two settings doors are not redundant. The RESOLVER answers "what value applies here", walking
// the cascade; it cannot answer "did THIS project override it", because a resolved value that equals
// the default is indistinguishable from an absent override — and that distinction is the whole
// content of the Clear-override button. The STORE's snapshot can, without a second query: it carries
// every row on the chain, so `Find(Scope.Project, key, path)` is a lookup in memory.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectDetailModel : PageModel
{
	// The stored path of LogSettings.RetentionDays — see its [Setting(Key = ...)]. It names the ROW
	// whose presence is the override.
	const string RetentionPath = "log.retention.days";

	readonly IProjectDirectory _projects;
	readonly AgentKeyAdminService _keys;
	readonly IHealthEndpointDirectory _health;
	readonly FeatureFlags _features;
	readonly ISettingsResolver _settings;
	readonly ISettingsStore _settingsStore;

	public ProjectDetailModel(
		IProjectDirectory projects,
		AgentKeyAdminService keys,
		IHealthEndpointDirectory health,
		FeatureFlags features,
		ISettingsResolver settings,
		ISettingsStore settingsStore)
	{
		_projects = projects;
		_keys = keys;
		_health = health;
		_features = features;
		_settings = settings;
		_settingsStore = settingsStore;
	}

	public bool DataEnabled => _features.IsEnabled(Feature.Data);

	// Effective retention as resolved by the cascade. Shown to the user as a hint
	// next to the per-project override field.
	public int EffectiveRetentionDays { get; private set; }
	public int? RetentionOverrideDays { get; private set; }

	// The retention this project would fall back to if its override were removed — the
	// cascade resolved from ABOVE the project (workspace → system), so the project's own
	// override row is excluded. This is the true "system default" value; the hint shows it
	// so an active override can never masquerade as the default (card ui-log-retention-settings-fix).
	public int DefaultRetentionDays { get; private set; }

	// The project's effective commit-view URL template (RepoSettings, cascaded). Empty = unset →
	// commit refs/hashes render as plain text. A literal {sha} placeholder is expanded per commit.
	public string CommitUrlTemplate { get; private set; } = "";

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
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
		Project = await _projects.GetAsync(ProjectKey);
		if (Project is null) return;

		// A just-minted key rides here across the Post/Redirect/Get from OnPostCreateKey and is
		// shown once; a refresh (no TempData) drops it. See Notice.CarryNewKey.
		NewKey = this.TakeNewKey();

		HealthEndpoints = await _health.ListForProjectAsync(ProjectKey);

		// Newest first. The service orders by CreatedAt ascending (its MCP callers page through keys
		// chronologically); reversing it is a rendering choice, made here, over rows already in hand —
		// not a second query.
		Keys = [.. (await _keys.ListByProjectAsync(ProjectKey)).OrderByDescending(k => k.CreatedAt)];

		// Effective LogSettings via cascade (project → workspace → system).
		var isSystem = string.Equals(ProjectKey, "$system", StringComparison.Ordinal);
		var effective = await _settings.GetAsync<LogSettings>(Scope.Project, ProjectKey);
		EffectiveRetentionDays = isSystem ? effective.SystemRetainDays : effective.RetentionDays;

		// The fallback default: same cascade but started at Workspace scope, which never reads
		// the project's own override row. Equals EffectiveRetentionDays when there is no override.
		var fallback = await _settings.GetAsync<LogSettings>(Scope.Workspace, WorkspaceKey);
		DefaultRetentionDays = isSystem ? fallback.SystemRetainDays : fallback.RetentionDays;

		// Has the project explicitly overridden its own retention? The snapshot's Find is a dictionary
		// hit, not a query — it holds every row on the chain already.
		var chain = await _settingsStore.LoadChainAsync(Scope.Project, ProjectKey);
		var overrideRow = chain.Find(Scope.Project, ProjectKey, RetentionPath);
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
			this.NotifySuccess("Commit template cleared.");
			return Self();
		}

		var newSettings = oldSettings with { CommitUrlTemplate = trimmed };
		var userIdRaw = User.FindFirst(PetBox.Core.Auth.PetBoxClaims.UserId)?.Value;
		long? userId = long.TryParse(userIdRaw, out var uid) ? uid : null;
		await _settings.SetAsync(Scope.Project, ProjectKey, newSettings, oldSettings, userId);
		this.NotifySuccess("Commit template saved.");
		return Self();
	}

	// Drop the project's own commit-view override so it falls back up the cascade.
	public async Task<IActionResult> OnPostClearCommitTemplateAsync()
	{
		await _settings.ResetAsync<RepoSettings>(Scope.Project, ProjectKey, nameof(RepoSettings.CommitUrlTemplate));
		this.NotifySuccess("Commit template cleared.");
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
		this.NotifySuccess("Retention updated.");
		return Self();
	}

	public async Task<IActionResult> OnPostClearRetentionAsync()
	{
		await _settings.ResetAsync<LogSettings>(Scope.Project, ProjectKey, nameof(LogSettings.RetentionDays));
		this.NotifySuccess("Retention override cleared.");
		return Self();
	}

	RedirectResult Self() => Redirect(Routes.ProjectSettings(WorkspaceKey, ProjectKey));

	public async Task<IActionResult> OnPostCreateHealthEndpointAsync(string url, int? intervalSeconds)
	{
		// The URL rule and the interval floor live in the directory — they are properties of what the
		// poller can honour, not of this form.
		var result = await _health.AddAsync(ProjectKey, url, intervalSeconds, User.Identity?.Name);
		if (result is HealthEndpointAddResult.Refused refused)
		{
			ErrorMessage = refused.Reason;
			await OnGetAsync();
			return Page();
		}

		this.NotifySuccess("Health endpoint added.");
		return Self();
	}

	public async Task<IActionResult> OnPostDeleteHealthEndpointAsync(long id)
	{
		// The project is welded into the DELETE inside the directory — a forged id belonging to another
		// project matches nothing.
		await _health.DeleteAsync(id, ProjectKey);
		this.NotifySuccess("Health endpoint deleted.");
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

		// Scopes are canonicalized before the mint — AgentKeyAdminService.MintAsync takes an already
		// validated set (the same contract its MCP callers honour), and the checkbox list is the only
		// intended input, so a typed one gets told so.
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

		var minted = await _keys.MintAsync(new AgentKeyMint(name, valid, ProjectKey));
		switch (minted)
		{
			case KeyMintResult.Minted m:
				// PRG: carry the one-time key across a redirect to the clean project URL (no lingering
				// ?handler=CreateKey a refresh would re-POST) — the key still shows exactly once.
				this.CarryNewKey(m.Key.Key);
				return Self();
			case KeyMintResult.NotFound nf:
				ErrorMessage = nf.Reason;
				await OnGetAsync();
				return Page();
			default:
				ErrorMessage = ((KeyMintResult.Refused)minted).Reason;
				await OnGetAsync();
				return Page();
		}
	}

	public async Task<IActionResult> OnPostRevokeKeyAsync(string keyValue)
	{
		// Project-confined inside the DELETE: a forged POST naming a SIBLING project's key (this admin
		// may hold WorkspaceAdmin over a dozen) matches zero rows.
		await _keys.RevokeForProjectAsync(keyValue, ProjectKey);
		this.NotifySuccess("API key revoked.");
		return Self();
	}

	// Edit the scopes of an existing key in place (scopes were previously fixed at
	// mint time — finding D5). Same validation as minting: known scopes, at least one — enforced in
	// the service, so this page cannot forget it and neither can the next caller.
	public async Task<IActionResult> OnPostUpdateKeyScopesAsync(string keyValue, string[]? scopes)
	{
		var result = await _keys.SetScopesForProjectAsync(keyValue, ProjectKey, scopes ?? []);
		if (result is KeyUpdateResult.Refused refused)
		{
			ErrorMessage = refused.Reason;
			await OnGetAsync();
			return Page();
		}

		this.NotifySuccess("Key scopes updated.");
		return Self();
	}

	// Delete the project and everything it owns in the Core DB (keys, health endpoints,
	// data/log/board/memory metadata, relations, settings). The cascade, the reserved-project
	// refusal and the workspace ownership check all live in IProjectDirectory.DeleteAsync — the
	// workspace is part of the ADDRESS there, so a forged POST naming another tenant's project
	// matches nothing.
	public async Task<IActionResult> OnPostDeleteAsync()
	{
		var result = await _projects.DeleteAsync(WorkspaceKey, ProjectKey);

		switch (result)
		{
			case ProjectChangeResult.Refused refused:
				ErrorMessage = refused.Reason;
				await OnGetAsync();
				return Page();
			case ProjectChangeResult.NotFound:
				ErrorMessage = "Project not found.";
				await OnGetAsync();
				return Page();
			default:
				this.NotifySuccess($"Project '{ProjectKey}' deleted.");
				return Redirect(Routes.WorkspaceAdminProjects(WorkspaceKey));
		}
	}
}
