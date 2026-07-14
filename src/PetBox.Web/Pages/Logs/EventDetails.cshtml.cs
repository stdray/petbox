using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Log.Core;
using PetBox.Log.Core.Data;

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
// {workspaceKey} either (only {projectKey}/{logName}/{id}), so [Authorize(Policy =
// "ApiKeyOrCookie")] only proves ONE of the two schemes authenticated — LogApi.
// AuthorizeProjectViewerAsync (the same method LiveTailAsync calls) is what proves the caller may
// read THIS project: an api key by project claim + logs:query scope, a cookie session by
// workspace-Viewer-or-better role. Bridging its minimal-API IResult into this MVC action via
// IResult.ExecuteAsync keeps the two endpoints on ONE authorization implementation rather than a
// second copy that could quietly drift from the first.
[Authorize(Policy = "ApiKeyOrCookie")]
public sealed class EventDetailsModel : PageModel
{
	readonly ILogStore _logStore;
	readonly IProjectCatalog _catalog;

	public EventDetailsModel(ILogStore logStore, IProjectCatalog catalog)
	{
		_logStore = logStore;
		_catalog = catalog;
	}

	public async Task<IActionResult> OnGetAsync(string projectKey, string logName, long id, CancellationToken ct)
	{
		if (await LogApi.AuthorizeProjectViewerAsync(HttpContext, projectKey, _catalog, ct) is { } forbid)
		{
			await forbid.ExecuteAsync(HttpContext);
			return new EmptyResult();
		}

		if (!await _logStore.ExistsAsync(projectKey, logName, ct))
			return NotFound();

		// The id lookup runs against THIS project+log's own SQLite file (one file per
		// project/log pair) — an id from another project's log simply does not exist here, so
		// there is no cross-project row to leak even before AuthorizeProjectViewerAsync's
		// project-scope check is considered.
		using var logDb = _logStore.NewEnsuredContext(projectKey, logName);
		var record = await logDb.LogEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
		if (record is null)
			return NotFound();

		return Partial("_EventDetails", LogEntryViewModel.FromRecord(record));
	}
}
