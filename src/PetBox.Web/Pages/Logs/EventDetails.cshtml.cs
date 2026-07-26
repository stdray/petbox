using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Log.Core;
using PetBox.Log.Core.Contract;

namespace PetBox.Web.Pages.Logs;

// Details on demand for ONE event, scoped to project+log — the fix for live-tail-row-details-
// unexpandable: LogApi.RenderEvent streams a bare <tr class="event-live"> with no paired
// <tr class="event-details"> sibling (unlike _EventRow.cshtml for a normal, non-live row), so
// ts/logs.ts's expand handler had nothing to toggle for a streamed row. logs.ts now fetches this
// fragment lazily on a live row's first click and inserts it as that missing sibling, then every
// later click just toggles what is already in the DOM — no refetch, and the fragment IS
// _EventDetails.cshtml, the exact partial a non-live row renders inline, so a live row's details
// (and its eq/ne filter chips) can never drift from what a reload of the page would show.
//
// Auth mirrors live-tail exactly, because it is the SAME cross-tenant surface: this route has no
// {workspaceKey} either (only {projectKey}/{logName}/{id}), so [Authorize(Policy = "ApiKeyOrCookie")]
// only proves ONE of the two schemes authenticated.
[Authorize(Policy = "ApiKeyOrCookie")]
// …and the DECLARATION is what proves the caller may read THIS project, for whichever scheme it turned
// out to be. ITenantAuthorizer answers an api-key principal on its project claim (+ sandbox
// containment) and a cookie principal on membership of the project's OWNING workspace at Viewer or
// better — asked of the catalog, because this route has no {workspaceKey} to bind against and
// ProjectWorkspaceBindingFilter therefore cannot help. Those are the exact two gates
// LogApi.AuthorizeProjectViewerAsync used to spell out by hand for this page and for live-tail; that
// method's last caller was this one, so it is deleted rather than left as a second implementation of a
// centralized decision.
//
// The route lives under /api and is fetched by script, so the refusal a programmatic caller gets is
// still a 403: TenantEnforcementMiddleware forbids through IAuthenticationService, and the "Smart"
// scheme answers per credential — a cookie session gets the /AccessDenied redirect, an api key gets the
// 403 (both pinned in LogEventDetailsApiTests).
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class EventDetailsModel : PageModel
{
	readonly ILogService _logs;

	// IProjectCatalog is GONE from this page's dependencies. It was here for exactly one reason — to
	// resolve the project's owning workspace inside AuthorizeProjectViewerAsync — and that lookup now
	// happens inside ITenantAuthorizer. A page still holding the catalog it no longer asks anything is an
	// invitation to write the check again.
	public EventDetailsModel(ILogService logs) => _logs = logs;

	public async Task<IActionResult> OnGetAsync(string projectKey, string logName, long id, CancellationToken ct)
	{
		// The ONE gate a declaration cannot carry, and it is the SCOPE axis rather than the tenant one: a
		// cookie carries no scopes at all, so this is asked only of the credential that has them. Same
		// call, same helper, as LiveTailAsync — crossing the two would be the hole (a cookie run through
		// the scope gate denies every browser; an api key run through the membership gate walks a
		// logs:query-less key in through a door meant for humans).
		if (LogApi.RequireLogsQueryScope(HttpContext) is { } forbid)
		{
			await forbid.ExecuteAsync(HttpContext);
			return new EmptyResult();
		}

		if (!await _logs.ExistsAsync(projectKey, logName, ct))
			return NotFound();

		// The id lookup runs against THIS project+log's own SQLite file (one file per
		// project/log pair) — an id from another project's log simply does not exist here, so
		// there is no cross-project row to leak even before AuthorizeProjectViewerAsync's
		// project-scope check is considered.
		var record = await _logs.GetEventAsync(projectKey, logName, id, ct);
		if (record is null)
			return NotFound();

		return Partial("_EventDetails", LogEntryViewModel.FromRecord(record));
	}
}
