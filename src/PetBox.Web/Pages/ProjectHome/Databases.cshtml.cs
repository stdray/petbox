using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI databases dashboard for a project (/ui/{ws}/{project}/databases).
// Read-only list from petbox.db metadata (cheap; no SQLite file opens).
// Create/delete + schema live in the admin area (reached via the gear).
[Authorize]
public sealed class DatabasesModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;

	public DatabasesModel(PetBoxDb db, FeatureFlags features)
	{
		_db = db;
		_features = features;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Core.Models.Project? Project { get; private set; }
	public bool DataEnabled => _features.IsEnabled(Feature.Data);
	public IReadOnlyList<DataDb> Dbs { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		Project = await _db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		if (Project is null || !DataEnabled) return;

		Dbs = await _db.DataDbs
			.Where(d => d.ProjectKey == ProjectKey)
			.OrderBy(d => d.Name)
			.ToListAsync(ct);
	}
}
