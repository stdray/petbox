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
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class SessionsModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly FeatureFlags _features;
	readonly ISessionStore _store;

	public SessionsModel(ICoreDbFactory f, FeatureFlags features, ISessionStore store)
	{
		_f = f;
		_features = features;
		_store = store;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	// The paging arg is 'pageNum', not 'page' — 'page' is a reserved route-key in Razor
	// Pages, so a ?page=N value never binds (see the Data-module table view lesson).
	[BindProperty(SupportsGet = true, Name = "pageNum")]
	public int PageNum { get; set; }

	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Query { get; set; }

	const int PageSize = 30;

	public Project? Project { get; private set; }
	public bool SessionsEnabled => _features.IsEnabled(Feature.Tasks);
	public IReadOnlyList<SessionHeader> Sessions { get; private set; } = [];
	public int Total { get; private set; }
	public bool HasNext { get; private set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		using var db = _f.Open();
		// Bind the project to the ROUTE workspace (see ProjectHome/Index) — rejects /ui/wsA/proj-of-wsB,
		// i.e. reading another tenant's session transcripts.
		Project = await db.Projects.FirstOrDefaultAsync(
			p => p.Key == ProjectKey && p.WorkspaceKey == WorkspaceKey, ct);
		if (Project is null || !SessionsEnabled) return;

		if (PageNum < 0) PageNum = 0;
		var page = await _store.ListPageAsync(ProjectKey, Query, PageNum, PageSize, ct);
		Sessions = page.Headers;
		HasNext = page.HasNext;
		Total = page.Total;
	}
}
