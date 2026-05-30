using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Sessions.Data;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one agent session (/ui/{ws}/{project}/sessions/{sessionId}).
// Shows the active plan blob (markdown content). Gated on Feature.Tasks.
[Authorize]
public sealed class SessionModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ISessionStore _store;

	public SessionModel(FeatureFlags features, ISessionStore store)
	{
		_features = features;
		_store = store;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "sessionId")]
	public string SessionId { get; set; } = string.Empty;

	public SessionRow? Session { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		Session = await _store.GetAsync(ProjectKey, SessionId, ct);
		if (Session is null) return NotFound();
		return Page();
	}
}
