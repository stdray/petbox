using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages.Dashboard;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly PetBoxDb _db;
	readonly INavigationContext _nav;
	readonly ISettingsResolver _settings;

	public IndexModel(PetBoxDb db, INavigationContext nav, ISettingsResolver settings)
	{
		_db = db;
		_nav = nav;
		_settings = settings;
	}

	public string WorkspaceKey { get; private set; } = "$system";
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

	public async Task OnGetAsync(CancellationToken ct)
	{
		WorkspaceKey = _nav.CurrentWorkspaceKey;
		var wsKey = WorkspaceKey;
		Projects = await _db.Projects.Where(p => p.WorkspaceKey == wsKey).OrderBy(p => p.Key).ToListAsync(ct);
		var projectKeys = Projects.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

		var dash = await _settings.GetAsync<DashboardSettings>(Scope.System, "$", ct);
		StaleSeconds = dash.StaleSeconds;

		LogCount = await CountByProjectAsync(_db.Logs.Where(l => projectKeys.Contains(l.ProjectKey)).Select(l => l.ProjectKey), ct);
		DbCount = await CountByProjectAsync(_db.DataDbs.Where(d => projectKeys.Contains(d.ProjectKey)).Select(d => d.ProjectKey), ct);
		KeyCount = await CountByProjectAsync(_db.ApiKeys.Where(k => projectKeys.Contains(k.ProjectKey)).Select(k => k.ProjectKey), ct);

		// Latest report per (Svc, Tags): Id is identity-ascending, so max Id = newest.
		var maxIds = await _db.HealthReports
			.GroupBy(r => new { r.Svc, r.Tags })
			.Select(g => g.Max(x => x.Id))
			.ToListAsync(ct);
		var latest = maxIds.Count == 0
			? []
			: await _db.HealthReports.Where(r => maxIds.Contains(r.Id)).ToListAsync(ct);

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
