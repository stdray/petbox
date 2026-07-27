using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
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
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
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

	// The provenance-bridge landing spot (card ui-search-sessions-hybrid, spec
	// search-one-engine-for-human-and-agent): a session-search hit carries a message ORDINAL
	// (SessionEpisodicHit.Message) the same way the agent's session_get {fromOrdinal} does —
	// a human clicking a hit gets the SAME window, not just the session's header. Absent/<=1
	// renders the whole transcript (today's behavior, unchanged).
	[BindProperty(SupportsGet = true, Name = "fromOrdinal")]
	public long? FromOrdinal { get; set; }

	public SessionSnapshot? Session { get; private set; }

	// The content actually rendered: the full transcript, or — when `fromOrdinal` points past
	// the first message — the suffix starting there (SessionSnapshot.ContentFrom, the SAME
	// slice session_get's fromOrdinal window renders, so the two surfaces can't disagree).
	public string DisplayContent { get; private set; } = string.Empty;

	// True when the page is showing a suffix, not the whole transcript — drives the "jumped to
	// message N — view full session" banner.
	public bool IsWindowed { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		// The project↔workspace binding (a member of wsA must not read wsB's transcripts via
		// /ui/wsA/proj-of-wsB/sessions/{id}) is enforced for EVERY page carrying both route values by
		// ProjectWorkspaceBindingFilter, before this handler runs — so it is not re-checked here.

		// Accept a full id or a unique prefix (the short form used in digests/search snippets);
		// a miss or an ambiguous prefix has no single page to show → NotFound.
		var resolved = await _store.ResolveIdAsync(ProjectKey, SessionId, ct);
		if (resolved.Match is null) return NotFound();
		SessionId = resolved.Match; // canonicalize for display/links

		Session = await _store.GetAsync(ProjectKey, SessionId, ct);
		if (Session is null) return NotFound();

		IsWindowed = FromOrdinal is > 1;
		DisplayContent = IsWindowed ? Session.ContentFrom(FromOrdinal!.Value) : Session.Content;
		return Page();
	}
}
