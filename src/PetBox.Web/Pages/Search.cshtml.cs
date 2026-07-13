using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Search;

namespace PetBox.Web.Pages;

// Cross-scope task search results (/ui/search?q=...) — the destination of the global top-nav
// search box (_Layout). Fans the read out across every workspace/project the user can reach
// (CrossScopeTaskSearchService); this page renders the flat, ordered hit list (exact matches
// first, then full-text, each project's own relevance order preserved) through the SAME
// reusable table component TaskBoard's own table view mode uses (_TaskTable.cshtml,
// board-view-mode-framework's direct reuse ask) — with workspace/project/board columns turned
// on, since a hit's location isn't implicit here the way it is on a single board's page.
[Authorize]
public sealed class SearchModel(CrossScopeTaskSearchService search) : PageModel
{
	// GET-bound query param; an omitted/empty `q` binds to null (empty-form-field gotcha),
	// so the empty-state check below is a null-or-empty guard, never a bare .Trim().
	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Q { get; set; }

	public IReadOnlyList<CrossScopeSearchHit> Hits { get; private set; } = [];

	// The reusable table's row shape (Pages/Shared/_TaskTable.cshtml), projected from Hits. No
	// per-project MethodologyRuntime is available at this fan-out (many projects, potentially
	// many methodologies), so Status renders as a plain outline badge — exactly what this page
	// already showed before the table reuse, just laid out as a table column instead of a
	// hand-rolled badge in a <li>. Closed uses the project-wide preset scan (MethodologyPresets.
	// IsTerminalSlug) — an approximation for a defined-kind's custom terminal slug, acceptable
	// here since active-only filtering on the search page is a bonus the row data enables for
	// free, not a promised guarantee.
	public IReadOnlyList<TaskTableRow> Rows { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(Q)) return;
		Hits = await search.SearchAsync(Q, ct);
		Rows = Hits.Select(h => new TaskTableRow(
			NodeId: h.NodeId, Key: h.Key, Title: h.Title, Url: h.Url, Type: h.Type,
			StatusSlug: h.Status, StatusDisplay: h.Status, StatusCssClass: "badge-outline", StatusShow: true,
			Closed: MethodologyPresets.IsTerminalSlug(h.Status),
			Priority: h.Priority, Tags: h.Tags ?? [], CreatedAt: null, UpdatedAt: h.UpdatedAt,
			Delivery: h.Delivery,
			// board-terminal-negative-visible: same project-wide preset approximation Closed
			// already uses above (no per-project MethodologyRuntime available at this fan-out).
			// IsSpecBoard is left at its default false (review finding: strikethrough is spec-only
			// now) — cheaply telling a spec hit from any other kind would need a per-project runtime
			// this fan-out doesn't have, and Status is already always shown here (StatusShow: true
			// above), so the redundancy argument holds without it too.
			TerminalCancel: MethodologyPresets.KindOfSlug(h.Status) == StatusKind.TerminalCancel,
			Workspace: h.Workspace, ProjectKey: h.ProjectKey, Board: h.Board)).ToList();
	}
}
