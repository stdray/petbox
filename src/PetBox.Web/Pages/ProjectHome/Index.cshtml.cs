using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.ProjectHome;

// Per-project dashboard — the project landing (/ui/{ws}/{project}). Logs moved to
// /ui/{ws}/{project}/logs. Shows cheap counts + this project's latest health.
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class IndexModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly IProjectDirectory _projects;
	readonly ISettingsResolver _settings;
	readonly IConfigDbFactory _configFactory;
	readonly ITasksService _tasks;
	readonly FeatureFlags _features;

	public IndexModel(
		ICoreDbFactory f,
		IProjectDirectory projects,
		ISettingsResolver settings,
		IConfigDbFactory configFactory,
		ITasksService tasks,
		FeatureFlags features)
	{
		_f = f;
		_projects = projects;
		_settings = settings;
		_configFactory = configFactory;
		_tasks = tasks;
		_features = features;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Core.Models.Project? Project { get; private set; }
	public int LogCount { get; private set; }
	public int DbCount { get; private set; }
	public int KeyCount { get; private set; }
	public int ConfigCount { get; private set; }
	public int TaskBoardCount { get; private set; }
	public bool TasksEnabled => _features.IsEnabled(Feature.Tasks);
	public bool ConfigEnabled => _features.IsEnabled(Feature.Config);
	// Whether the viewer may reach the workspace-admin project pages (api keys, data admin).
	// Counters that only exist in the admin zone degrade to a plain badge for non-admins.
	public bool CanAdminWorkspace { get; private set; }
	public int StaleSeconds { get; private set; } = 300;
	public IReadOnlyList<HealthRow> Health { get; private set; } = [];

	public sealed record HealthRow(
		string Svc, string? Name, IReadOnlyDictionary<string, string> OtherTags,
		string? Version, string? Sha, string Status, DateTime ReceivedAt);

	public async Task OnGetAsync(CancellationToken ct)
	{
		// The project↔workspace binding — a member of wsA reading wsB's project via /ui/wsA/proj-of-wsB
		// — is enforced for EVERY page carrying both route values by ProjectWorkspaceBindingFilter
		// (Program.cs), which 404s before this handler runs. The directory welds the workspace into the
		// lookup anyway: the filter is pipeline code, this is the statement itself, and the ten hand-
		// written copies of this rule that drifted are exactly why it now lives in one service.
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null) return;

		// The counters below have no service door yet (logs, data dbs, api keys, health reports), so
		// this page still opens core.db for them — ONE connection, after the project is known.
		using var db = _f.Open();

		LogCount = await db.Logs.CountAsync(l => l.ProjectKey == ProjectKey, ct);
		DbCount = await db.DataDbs.CountAsync(d => d.ProjectKey == ProjectKey, ct);
		KeyCount = await db.ApiKeys.CountAsync(k => k.ProjectKey == ProjectKey, ct);
		CanAdminWorkspace = User.CanAdminWorkspace(WorkspaceKey);

		if (ConfigEnabled)
			ConfigCount = CountProjectConfigBindings();

		if (TasksEnabled)
			TaskBoardCount = (await _tasks.ListBoardsAsync(ProjectKey, ct)).Count;

		var dash = await _settings.GetAsync<DashboardSettings>(Scope.System, "$", ct);
		StaleSeconds = dash.StaleSeconds;

		var maxIds = await db.HealthReports
			.GroupBy(r => new { r.Svc, r.Tags })
			.Select(g => g.Max(x => x.Id))
			.ToListAsync(ct);
		var latest = maxIds.Count == 0
			? []
			: await db.HealthReports.Where(r => maxIds.Contains(r.Id)).ToListAsync(ct);

		var rows = new List<HealthRow>();
		foreach (var r in latest)
		{
			var tags = HealthTags.Parse(r.Tags);
			if (!tags.TryGetValue("project", out var proj) || proj != ProjectKey) continue;
			var other = tags.Where(kv => kv.Key != "project")
				.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
			rows.Add(new HealthRow(r.Svc, r.Name, other, r.Version, r.Sha, r.Status, r.ReceivedAt));
		}
		Health = rows.OrderBy(h => h.Svc, StringComparer.Ordinal).ToList();
	}

	// Non-deleted config bindings tagged for this project ("project:{key}") in the workspace
	// config DB. Mirrors the project-scope filter the /config page applies (Config.IndexModel).
	int CountProjectConfigBindings()
	{
		var projectTag = $"project:{ProjectKey}";
		using var configDb = _configFactory.NewConfigDb(WorkspaceKey);
		return configDb.Bindings
			.Where(b => !b.IsDeleted)
			.AsEnumerable()
			.Count(b => HasTag(b.Tags, projectTag));
	}

	static bool HasTag(string tags, string tag)
	{
		foreach (var t in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}
}
