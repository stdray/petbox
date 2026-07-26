using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
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
// WHY `identity` AND NOT A TENANT SOURCE, on the one page in this tree that deliberately crosses
// every tenant the caller can reach. There is nothing to declare a source FROM: the route is a bare
// /ui/search, `q` is a search string and not a tenant, and the page never accepts a
// workspace/project from the request at all. The extent of the fan-out is the CALLER —
// NavigationContext.ProjectsByWorkspace, already filtered to their own memberships (sysadmin sees
// all), which CrossScopeTaskSearchService's own header calls out as the reason the fan-out is legal.
//
// So the subject is the caller and the answer is a fact about their own reach, exactly like
// /api/ui/board-filter-prefs and whoami. What this exemption does NOT do is loosen anything: were
// ProjectsByWorkspace ever to stop being membership-filtered, this page would leak every board in
// the installation and no declaration on it would have caught that — the guard for that lives with
// the enumeration, not here.
[TenantExempt(TenantExemption.Identity,
	"fans out over the caller's OWN reachable projects (NavigationContext.ProjectsByWorkspace, "
	+ "membership-filtered); the request names no workspace or project to scope it to")]
public sealed class SearchModel(CrossScopeTaskSearchService search) : PageModel
{
	// GET-bound query param; an omitted/empty `q` binds to null (empty-form-field gotcha),
	// so the empty-state check below is a null-or-empty guard, never a bare .Trim().
	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Q { get; set; }

	public IReadOnlyList<CrossScopeSearchHit> Hits { get; private set; } = [];

	// The reusable table's row shape (Pages/Shared/_TaskTable.cshtml), projected from Hits. This
	// fan-out spans many projects/methodologies, so there is no single MethodologyRuntime to render
	// the status LABEL through — Status stays a plain outline badge (raw slug), exactly what this
	// page already showed before the table reuse. Closed / TerminalCancel, however, are NOT a
	// far-side guess: each hit already carries its authoritative per-board classification
	// (CrossScopeSearchHit.StatusKind), computed inside the search branch through the ONE classifier
	// StatusKindOf (spec tasks-status-kind-classifier). The old code approximated them here with the
	// board-less preset scan (MethodologyPresets.IsTerminalSlug/KindOfSlug), which diverged from the
	// authority on a custom methodology's own terminal slugs — a custom terminal status read as live.
	public IReadOnlyList<TaskTableRow> Rows { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(Q)) return;
		Hits = await search.SearchAsync(Q, ct);
		Rows = Hits.Select(h => new TaskTableRow(
			NodeId: h.NodeId, Key: h.Key, Title: h.Title, Url: h.Url, Type: h.Type,
			StatusSlug: h.Status, StatusDisplay: h.Status, StatusCssClass: "badge-outline", StatusShow: true,
			Closed: h.StatusKind is StatusKind.TerminalOk or StatusKind.TerminalCancel,
			Priority: h.Priority, Tags: h.Tags ?? [], CreatedAt: null, UpdatedAt: h.UpdatedAt,
			Delivery: h.Delivery,
			// board-terminal-negative-visible: the terminal-CANCEL half of the same authoritative
			// per-board classification Closed reads above — struck through on EVERY board kind, not
			// only spec, matching the invariant every other view enforces.
			TerminalCancel: h.StatusKind == StatusKind.TerminalCancel,
			Workspace: h.Workspace, ProjectKey: h.ProjectKey, Board: h.Board)).ToList();
	}
}
