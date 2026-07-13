using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages.Dashboard;

// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class IndexModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly INavigationContext _nav;
	readonly ISettingsResolver _settings;

	public IndexModel(ICoreDbFactory f, INavigationContext nav, ISettingsResolver settings)
	{
		_f = f;
		_nav = nav;
		_settings = settings;
	}

	public string WorkspaceKey { get; private set; } = "$system";
	// Whether the viewer may reach the workspace-admin project pages (api keys). The per-project
	// api-keys counter degrades to a plain badge for non-admins instead of a redirect-to-Login link.
	public bool CanAdminWorkspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public IReadOnlyDictionary<string, IReadOnlyList<HealthRow>> ByProject { get; private set; }
		= new Dictionary<string, IReadOnlyList<HealthRow>>();
	public int StaleSeconds { get; private set; } = 300;

	// Cheap per-project counts (petbox.db metadata only — no log/data file opens).
	public IReadOnlyDictionary<string, int> LogCount { get; private set; } = new Dictionary<string, int>();
	public IReadOnlyDictionary<string, int> DbCount { get; private set; } = new Dictionary<string, int>();
	public IReadOnlyDictionary<string, int> KeyCount { get; private set; } = new Dictionary<string, int>();

	public sealed record HealthRow(
		string Svc, string? Name, IReadOnlyDictionary<string, string> OtherTags,
		string? Version, string? Sha, string? BuildDate, string Status, DateTime ReceivedAt);

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		using var db = _f.Open();
		// The /ui/{workspaceKey} catch-all must NOT silently render the resolved default
		// workspace (previously $system) for an unknown or non-member key. NavigationContext
		// only resolves the route key when the user is a member of it (ResolveWorkspace step 1);
		// a junk/inaccessible key falls through to the cookie/claim/first-membership fallback,
		// so the resolved key differs from the route key → treat it as not found. A VALID user
		// on a real key they belong to resolves back to that same key and renders normally.
		var routeKey = RouteData.Values["workspaceKey"]?.ToString();
		var resolved = _nav.CurrentWorkspaceKey;
		if (string.IsNullOrEmpty(routeKey) || !string.Equals(resolved, routeKey, StringComparison.Ordinal))
			return NotFound();
		WorkspaceKey = routeKey;

		CanAdminWorkspace = User.CanAdminWorkspace(WorkspaceKey);
		var wsKey = WorkspaceKey;
		// Ensure the workspace memory container exists before the "Shared memory" card links
		// to it — non-$system workspaces are lazy-created (MCP write or this render path);
		// $system is already seeded by M028/M031 so this is a no-op there.
		await WorkspaceMemory.EnsureContainerAsync(db, wsKey, ct);
		// Workspace memory containers ($workspace / $ws-*) are not user projects — keep them
		// out of the project grid (no logs/dbs/keys) and surface the current one as the
		// dedicated "Shared memory" entry the view renders instead.
		Projects = (await db.Projects
				.Where(p => p.WorkspaceKey == wsKey)
				.OrderBy(p => p.Key).ToListAsync(ct))
			.Where(p => !WorkspaceMemory.IsWorkspaceContainer(p.Key))
			.ToList();
		var projectKeys = Projects.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

		var dash = await _settings.GetAsync<DashboardSettings>(Scope.System, "$", ct);
		StaleSeconds = dash.StaleSeconds;

		LogCount = await CountByProjectAsync(db.Logs.Where(l => projectKeys.Contains(l.ProjectKey)).Select(l => l.ProjectKey), ct);
		DbCount = await CountByProjectAsync(db.DataDbs.Where(d => projectKeys.Contains(d.ProjectKey)).Select(d => d.ProjectKey), ct);
		KeyCount = await CountByProjectAsync(db.ApiKeys.Where(k => projectKeys.Contains(k.ProjectKey)).Select(k => k.ProjectKey), ct);

		// Latest report per service. Identity is (project tag, Svc) — the rest of the
		// tags (host, elapsedMs, reason, …) are volatile payload of a single report, so
		// grouping by the raw Tags string would resurface the whole history. The project
		// tag lives inside Tags, hence the in-memory pass. Id is identity-ascending:
		// max Id = newest.
		var slim = await db.HealthReports
			.Select(r => new { r.Id, r.Svc, r.Tags })
			.ToListAsync(ct);
		var maxIds = slim
			.GroupBy(r => (r.Svc, Project: HealthTags.Parse(r.Tags).GetValueOrDefault("project", "")))
			.Select(g => g.Max(x => x.Id))
			.ToList();
		var latest = maxIds.Count == 0
			? []
			: await db.HealthReports.Where(r => maxIds.Contains(r.Id)).ToListAsync(ct);

		var byProject = new Dictionary<string, List<HealthRow>>(StringComparer.Ordinal);
		foreach (var r in latest)
		{
			var tags = HealthTags.Parse(r.Tags);
			if (!tags.TryGetValue("project", out var proj) || !projectKeys.Contains(proj)) continue;
			var other = tags.Where(kv => kv.Key != "project")
				.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
			if (!byProject.TryGetValue(proj, out var list))
				byProject[proj] = list = [];
			list.Add(new HealthRow(r.Svc, r.Name, other, r.Version, r.Sha, r.BuildDate, r.Status, r.ReceivedAt));
		}
		ByProject = byProject.ToDictionary(
			kv => kv.Key,
			kv => (IReadOnlyList<HealthRow>)kv.Value.OrderBy(h => h.Svc, StringComparer.Ordinal).ToList(),
			StringComparer.Ordinal);

		return Page();
	}

	static async Task<IReadOnlyDictionary<string, int>> CountByProjectAsync(IQueryable<string> projectKeys, CancellationToken ct)
	{
		var rows = await projectKeys
			.GroupBy(k => k)
			.Select(g => new { Key = g.Key, Count = g.Count() })
			.ToListAsync(ct);
		return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
	}
}
