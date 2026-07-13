using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one agent session (/ui/{ws}/{project}/sessions/{sessionId}).
// Shows the active plan blob (markdown content). Gated on Feature.Tasks.
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class SessionModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ISessionStore _store;
	readonly ICoreDbFactory _f;

	public SessionModel(FeatureFlags features, ISessionStore store, ICoreDbFactory f)
	{
		_features = features;
		_store = store;
		_f = f;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "sessionId")]
	public string SessionId { get; set; } = string.Empty;

	public SessionSnapshot? Session { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		// Bind the project to the ROUTE workspace: the WorkspaceViewer policy only proves membership
		// in {workspaceKey}, so without this a member of wsA could read wsB's session TRANSCRIPTS via
		// /ui/wsA/proj-of-wsB/sessions/{id} (workspace-access-isolation).
		using (var core = _f.Open())
		{
			var inWorkspace = await core.Projects.AnyAsync(
				p => p.Key == ProjectKey && p.WorkspaceKey == WorkspaceKey, ct);
			if (!inWorkspace) return NotFound();
		}

		// Accept a full id or a unique prefix (the short form used in digests/search snippets);
		// a miss or an ambiguous prefix has no single page to show → NotFound.
		var resolved = await _store.ResolveIdAsync(ProjectKey, SessionId, ct);
		if (resolved.Match is null) return NotFound();
		SessionId = resolved.Match; // canonicalize for display/links

		Session = await _store.GetAsync(ProjectKey, SessionId, ct);
		if (Session is null) return NotFound();
		return Page();
	}
}
