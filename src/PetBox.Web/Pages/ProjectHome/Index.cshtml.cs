using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Web.Pages.ProjectHome;

// Per-project dashboard — the project landing (/ui/{ws}/{project}). Logs moved to
// /ui/{ws}/{project}/logs. Shows cheap counts + this project's latest health.
[Authorize]
public sealed class IndexModel : PageModel
{
	readonly PetBoxDb _db;
	readonly ISettingsResolver _settings;

	public IndexModel(PetBoxDb db, ISettingsResolver settings)
	{
		_db = db;
		_settings = settings;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Core.Models.Project? Project { get; private set; }
	public int LogCount { get; private set; }
	public int DbCount { get; private set; }
	public int KeyCount { get; private set; }
	public int StaleSeconds { get; private set; } = 300;
	public IReadOnlyList<HealthRow> Health { get; private set; } = [];

	public sealed record HealthRow(
		string Svc, string? Name, IReadOnlyDictionary<string, string> OtherTags,
		string? Version, string? Sha, string Status, DateTime ReceivedAt);

	public async Task OnGetAsync(CancellationToken ct)
	{
		Project = await _db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		if (Project is null) return;

		LogCount = await _db.Logs.CountAsync(l => l.ProjectKey == ProjectKey, ct);
		DbCount = await _db.DataDbs.CountAsync(d => d.ProjectKey == ProjectKey, ct);
		KeyCount = await _db.ApiKeys.CountAsync(k => k.ProjectKey == ProjectKey, ct);

		var dash = await _settings.GetAsync<DashboardSettings>(Scope.System, "$", ct);
		StaleSeconds = dash.StaleSeconds;

		var maxIds = await _db.HealthReports
			.GroupBy(r => new { r.Svc, r.Tags })
			.Select(g => g.Max(x => x.Id))
			.ToListAsync(ct);
		var latest = maxIds.Count == 0
			? []
			: await _db.HealthReports.Where(r => maxIds.Contains(r.Id)).ToListAsync(ct);

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
}
