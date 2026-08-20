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
// (CrossScopeTaskSearchService). ui-search-group-by-project: the true order is project-by-
// project, not a cross-project relevance ranking (CrossScopeTaskSearchService.cs:126-133), so this
// page makes that order VISIBLE — an exact-identifier hit (pasted slug/NodeId) stays first and
// ungrouped, everything else is bucketed into a collapsible per-project section. Every row still
// renders through the SAME reusable table component TaskBoard's own table view mode uses
// (_TaskTable.cshtml, board-view-mode-framework's direct reuse ask) — with workspace/project/board
// columns turned on, since a hit's location isn't implicit here the way it is on a single board's
// page.
[Authorize]
// WHY `identity` AND NOT A TENANT SOURCE, on the one page in this tree that deliberately crosses
// every tenant the caller can reach. There is nothing to declare a source FROM: the route is a bare
// /ui/search, `q` is a search string and not a tenant, and the page never accepts a
// workspace/project from the request at all. The extent of the fan-out is the CALLER —
// NavigationContext.ProjectsByWorkspace, already filtered to their own memberships, which
// CrossScopeTaskSearchService's own header calls out as the reason the fan-out is legal. Since spec
// tenant-visibility-by-membership that filter has NO sysadmin arm on this route: /ui/search is a
// user-zone path, so the enumeration behind it is memberships even for a holder of the system
// permission (the admin zone keeps the full catalog; this page is not in it).
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

	// ui-search-locator-honest-boundary: a STATED fact, not a hedge. This fan-out has no single
	// ranked pool to page (each project runs its own capped full-text leg and the merge is a
	// project-ordered concatenation, not a scalar relevance order) — see CrossScopeTaskSearchService's
	// own header for why true keyset paging across an arbitrary number of projects is a separate,
	// larger design than the per-container pool this page's memory/sessions siblings already page.
	// The OLD name (PossiblyTruncated) framed this as a guess ("possibly" truncated); it never was
	// one — MaxResults=50 is an exact, known cap the merge enforces (CrossScopeTaskSearchService.cs's
	// own `if (merged.Count >= MaxResults) break`), so reaching it is a hard fact about what this
	// screen is showing, stated directly. What stays honestly UNCLAIMED is whether more matches exist
	// beyond the cap — this property says only "the locator's ceiling was reached", never "and that's
	// everything" or "and there might be more" (the "don't infer the end from silence" principle,
	// applied where a hard pool-boundary fact IS available, so it's the one thing stated).
	public bool AtLocatorCeiling => Hits.Count >= CrossScopeTaskSearchService.MaxResults;

	// The reusable table's row shape (Pages/Shared/_TaskTable.cshtml). This fan-out spans many
	// projects/methodologies, so there is no single MethodologyRuntime to render the status LABEL
	// through — Status stays a plain outline badge (raw slug), exactly what this page already showed
	// before the table reuse. Closed / TerminalCancel, however, are NOT a far-side guess: each hit
	// already carries its authoritative per-board classification (CrossScopeSearchHit.StatusKind),
	// computed inside the search branch through the ONE classifier StatusKindOf (spec
	// tasks-status-kind-classifier). The old code approximated them here with the board-less preset
	// scan (MethodologyPresets.IsTerminalSlug/KindOfSlug), which diverged from the authority on a
	// custom methodology's own terminal slugs — a custom terminal status read as live.
	//
	// ui-search-group-by-project: an exact-identifier hit (a pasted slug/NodeId) stays FIRST and
	// UNGROUPED — per this page's own header, that's ~98% of real queries, and burying it inside a
	// collapsed per-project section would cost the one path that must stay instant. ExactRows holds
	// those; Groups holds everything else, bucketed by project in the SAME order the fan-out already
	// merged them (ws then project key) — grouping only makes that true order VISIBLE, it does not
	// re-sort anything (card's own caution: this is view work, the fan-out itself is untouched).
	public IReadOnlyList<TaskTableRow> ExactRows { get; private set; } = [];
	public IReadOnlyList<SearchProjectGroup> Groups { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(Q)) return;
		Hits = await search.SearchAsync(Q, ct);

		var withRows = Hits.Select(h => (Hit: h, Row: ToRow(h))).ToList();
		ExactRows = withRows.Where(t => t.Hit.ExactMatch).Select(t => t.Row).ToList();
		Groups = withRows.Where(t => !t.Hit.ExactMatch)
			.GroupBy(t => (t.Hit.Workspace, t.Hit.ProjectKey))
			.Select(g => new SearchProjectGroup(
				Workspace: g.Key.Workspace,
				ProjectKey: g.Key.ProjectKey,
				Rows: g.Select(t => t.Row).ToList(),
				// ui-search-group-by-project item 2: "перейти к поиску в этом проекте" — the bridge to
				// the project's own tasks page, query carried over. This screen never grows the full
				// search the spec deliberately keeps out of it (cross-scope-search-is-an-identifier-
				// locator) — it only hands the caller off, query in hand, to where that search lives.
				SearchInProjectUrl: $"{Routes.ProjectTasks(g.Key.Workspace, g.Key.ProjectKey)}?q={Uri.EscapeDataString(Q)}"))
			.ToList();
	}

	static TaskTableRow ToRow(CrossScopeSearchHit h) => new(
		NodeId: h.NodeId, Key: h.Key, Title: h.Title, Url: h.Url, Type: h.Type,
		StatusSlug: h.Status, StatusDisplay: h.Status, StatusCssClass: "badge-outline", StatusShow: true,
		Closed: h.StatusKind is StatusKind.TerminalOk or StatusKind.TerminalCancel,
		Priority: h.Priority, Tags: h.Tags ?? [], CreatedAt: null, UpdatedAt: h.UpdatedAt,
		Delivery: h.Delivery,
		// board-terminal-negative-visible: the terminal-CANCEL half of the same authoritative
		// per-board classification Closed reads above — struck through on EVERY board kind, not
		// only spec, matching the invariant every other view enforces.
		TerminalCancel: h.StatusKind == StatusKind.TerminalCancel,
		Workspace: h.Workspace, ProjectKey: h.ProjectKey, Board: h.Board);
}

// ui-search-group-by-project: one collapsible section of the results page — every non-exact hit
// that landed in this Workspace/ProjectKey, in the fan-out's own order. SearchInProjectUrl is the
// bridge to the project's own (full) search; this screen only ever links to it, never grows one.
public sealed record SearchProjectGroup(string Workspace, string ProjectKey, IReadOnlyList<TaskTableRow> Rows, string SearchInProjectUrl);
