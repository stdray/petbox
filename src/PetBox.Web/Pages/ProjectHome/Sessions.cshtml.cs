using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI sessions list for a project (/ui/{ws}/{project}/sessions). Read-only
// list of the currently-active agent session plans. There is no catalog: one
// sessions file per project, written by agents via the session MCP tools.
// Gated on Feature.Tasks (sessions ship with the Tasks module).
[Authorize]
public sealed class SessionsModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly ISessionStore _store;

	public SessionsModel(PetBoxDb db, FeatureFlags features, ISessionStore store)
	{
		_db = db;
		_features = features;
		_store = store;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Project? Project { get; private set; }
	public bool SessionsEnabled => _features.IsEnabled(Feature.Tasks);
	public IReadOnlyList<SessionHeader> Sessions { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		Project = await _db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		if (Project is null || !SessionsEnabled) return;

		Sessions = await _store.ListAsync(ProjectKey, ct);
	}
}
