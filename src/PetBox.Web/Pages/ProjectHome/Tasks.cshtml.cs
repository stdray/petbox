using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Auth;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI tasks dashboard for a project (/ui/{ws}/{project}/tasks). With no `q` this is the
// original READ-ONLY list of named boards from petbox.db metadata (cheap; no per-board file
// opens) — UNCHANGED by ui-project-task-search (spec in-project-task-search-exists): that
// behavior is exactly what a bare visit to this URL kept getting before this card, and nothing
// about it is touched below.
//
// ui-project-task-search: a non-empty `q` now switches this SAME route into the project-wide
// task SEARCH results screen the spec found missing — the one screen the cross-scope locator's
// own "Search in this project" link (Pages/Search.cshtml.cs SearchInProjectUrl) already pointed
// at (`?q=` on this exact route) without anything here answering it. ROUTE CHOICE: same page,
// mode split on Query — not a separate route — mirroring EXACTLY how Sessions.cshtml
// (SessionsModel.IsSearchMode) and MemoryStore.cshtml (MemoryStoreModel.IsSearch) already draw
// this line: one URL, the presence of `q` picks the render. A dedicated `/tasks/search` route
// would be the one inconsistent shape among the three entities sharing one search engine (spec
// search-one-engine-for-human-and-agent) for no offsetting benefit — this project's board list is
// cheap enough that toggling the SAME page's mode costs nothing extra.
//
// Search mode reuses ITasksService.SearchNodesAsync (never TasksService.cs itself) — the same
// unified read tasks_search's own MCP adapter (TasksTools.SearchAsync) already calls — and mirrors
// that adapter's keyset-cursor/fingerprint/pool-order mechanics locally (see the cursor helpers
// below), the same "one small per-surface copy, not a shared low-level utility" shape
// MemoryStoreModel/SessionsModel already use for their own cursor axes.
//
// Rows render through the SAME reusable _TaskTable.cshtml the cross-scope locator's table uses
// (board-view-mode-framework) — no fourth copy of task-row markup, a direct decision of this card.
//
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
// Search mode runs INSIDE this same guard (Project is resolved via GetInWorkspaceAsync below
// BEFORE either branch runs) — never beside it, so a mismatched workspace/project URL surfaces no
// row in EITHER mode, exactly the same as the pre-existing board-list path.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class TasksModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;
	// Optional, same posture as every other search page's _uiState (SessionsModel/MemoryStoreModel):
	// resolves the caller's ui-search-ranking-mode-preference override of the UI edge default
	// (Speed). DI always supplies it; a bare unit-test construction may omit it.
	readonly IUiState? _uiState;

	public TasksModel(IProjectDirectory projects, FeatureFlags features, ITasksService tasks, IUiState? uiState = null)
	{
		_projects = projects;
		_features = features;
		_tasks = tasks;
		_uiState = uiState;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Project? Project { get; private set; }
	public bool TasksEnabled => _features.IsEnabled(Feature.Tasks);
	public IReadOnlyList<TaskBoardMeta> Boards { get; private set; } = [];

	// spec methodology-inactive-visibility: the project's current effective default instance —
	// a board whose own membership names an open instance other than this one is a full member
	// of a live process that just isn't the project's default right now. Computed here (not a
	// stored board flag) so the card template can compare identity directly.
	public string? EffectiveActiveInstance { get; private set; }

	// ui-project-task-search: search-mode bindings, all optional — an absent `q` leaves every one
	// of these at its default and the board-list branch below runs exactly as it always did.
	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Query { get; set; }

	[BindProperty(SupportsGet = true, Name = "cursor")]
	public string? Cursor { get; set; }

	// priority|created|updated|title|relevance — relevance is this mode's own default (a query is
	// always in play here, unlike the board-less listing TasksTools.SearchAsync also serves).
	[BindProperty(SupportsGet = true, Name = "sortBy")]
	public string? SortBy { get; set; }

	[BindProperty(SupportsGet = true, Name = "sortDesc")]
	public bool? SortDesc { get; set; }

	// ui-search-page-position-and-size: same control, same rationale as Sessions/MemoryStore.
	[BindProperty(SupportsGet = true, Name = "size")]
	public int? Size { get; set; }
	public int EffectiveSize => PageSizeOptions.Resolve(Size);

	[BindProperty(SupportsGet = true, Name = "pos")]
	public int Pos { get; set; }
	int EffectivePos => Pos < 0 ? 0 : Pos;

	public bool IsSearchMode => !string.IsNullOrWhiteSpace(Query);

	public IReadOnlyList<TaskTableRow> SearchRows { get; private set; } = [];
	public int Total { get; private set; }
	public int RangeFrom { get; private set; }
	public int RangeTo { get; private set; }
	public string? NextCursor { get; private set; }
	// WHY THE WALK STOPPED (spec result-set-pageable requirement 2) — "more" | "exhausted" |
	// "pool-boundary", the same three words every other search surface in this app uses.
	public string? Stop { get; private set; }
	public string? PoolBoundaryHint { get; private set; }
	public bool CursorWasReset { get; private set; }
	public SearchRetrievers? Retrievers { get; private set; }

	public string EffectiveSortBy => TaskSearchSortKeys.IsKnown(SortBy) ? SortBy! : TaskSearchSortKeys.Relevance;
	public bool EffectiveSortDesc => SortDesc ?? (EffectiveSortBy != TaskSearchSortKeys.Title);

	public async Task OnGetAsync(CancellationToken ct)
	{
		// The route workspace is welded into the lookup — the second rubicon behind
		// ProjectWorkspaceBindingFilter, not a replacement for it (see ProjectHome/Index). BOTH
		// modes below run behind this SAME guard: a mismatched workspace/project URL returns here,
		// before either branch has a chance to read a single row.
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !TasksEnabled) return;

		if (IsSearchMode)
		{
			await RunSearchAsync(ct);
			return;
		}

		Boards = await _tasks.ListBoardsAsync(ProjectKey, ct);
		EffectiveActiveInstance = await _tasks.ResolveDefaultMethodologyInstanceAsync(ProjectKey, ct);
	}

	async Task RunSearchAsync(CancellationToken ct)
	{
		var q = Query!.Trim();
		var axis = ParseSortBy(EffectiveSortBy);
		var desc = EffectiveSortDesc;

		// ui-search-ranking-mode-preference: same override every other UI search surface honours.
		var rankingMode = _uiState is not null ? (await _uiState.GetAsync(ct)).SearchRankingMode : SearchRankingMode.Speed;

		// Project-WIDE (Board: null) — the spec's own wording: "задачи ПРОЕКТА... в границах этого
		// проекта", every board, each row naming its own. PAGES the WHOLE ranked pool (spec
		// result-set-pageable), exactly as TasksTools.SearchAsync's own query mode does.
		var result = await _tasks.SearchNodesAsync(ProjectKey, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Query = q,
			Filter = new TaskNodeFilter(),
			Sort = (axis, desc),
			Limit = EffectiveSize,
			WholePool = true,
			BodyLen = 0,
			RankingMode = rankingMode,
		}, ct: ct);
		Retrievers = result.Retrievers;

		var fingerprint = SearchFingerprint(q, axis, desc, result.DataVersion);
		IReadOnlyList<TaskSearchHit> afterCursor = result.Hits;
		if (!string.IsNullOrWhiteSpace(Cursor))
		{
			try
			{
				var decoded = KeysetCursor.Decode(Cursor, fingerprint, "project-tasks-search");
				// THE ORDER COMMITMENT (spec result-set-pageable) — same check tasks_search's own
				// adapter makes before seeking: the fingerprint only proves the QUESTION is
				// unchanged, this proves the ranked ANSWER is still in the sequence the token names.
				if (result.PoolOrderHash is { } expectedOrder)
					decoded.AssertPoolOrder(expectedOrder, "project-tasks-search");
				afterCursor = KeysetCursor.Advance(
					result.Hits, decoded,
					h => (CursorSortValue(h, axis), h.Node.Key, h.Board),
					CursorSortComparison(axis), desc, "project-tasks-search");
			}
			catch (ArgumentException)
			{
				CursorWasReset = true;
				afterCursor = result.Hits;
			}
		}

		var page = afterCursor.Take(EffectiveSize).ToList();
		var classify = await ClassifyByBoardAsync(_tasks, ProjectKey, page, ct);
		SearchRows = page.Select(h => ToRow(WorkspaceKey, ProjectKey, h, classify(h))).ToList();
		Total = SearchRows.Count;
		var hasNext = afterCursor.Count > EffectiveSize;
		if (hasNext)
		{
			var last = page[^1];
			NextCursor = new KeysetCursor(fingerprint, CursorSortValue(last, axis), last.Node.Key, last.Board,
				result.PoolOrderHash ?? "").Encode();
		}
		// WHY THE WALK STOPPED — stated, never implied (card requirement 2).
		Stop = hasNext ? "more" : result.PoolBounded ? "pool-boundary" : "exhausted";
		PoolBoundaryHint = Stop == "pool-boundary" ? PoolBoundaryHintText : null;
		if (SearchRows.Count > 0) { RangeFrom = EffectivePos + 1; RangeTo = EffectivePos + SearchRows.Count; }
	}

	static TaskTableRow ToRow(string ws, string projectKey, TaskSearchHit h, StatusKind? statusKind) => new(
		NodeId: h.Node.NodeId, Key: h.Node.Key, Title: h.Node.Title, Url: h.Node.Url ?? "", Type: h.Node.Type,
		StatusSlug: h.Node.Status, StatusDisplay: h.Node.Status, StatusCssClass: "badge-outline", StatusShow: true,
		Closed: statusKind is StatusKind.TerminalOk or StatusKind.TerminalCancel,
		Priority: h.Node.Priority, Tags: h.Node.Tags, CreatedAt: null, UpdatedAt: h.Node.UpdatedAt,
		Delivery: h.Node.Delivery,
		TerminalCancel: statusKind == StatusKind.TerminalCancel,
		// ShowScopeColumns:true on this table (see the .cshtml) renders Workspace/ProjectKey too —
		// both constant across every row on THIS project's own page — plus Board, which DOES vary
		// (a project-wide search spans every board). Reusing the same three-column shape rather
		// than teaching _TaskTable a fourth "board-only" mode keeps this a parameter, not a copy.
		Workspace: ws, ProjectKey: projectKey, Board: h.Board);

	// Terminality classified through the ONE authority (spec tasks-status-kind-classifier), exactly
	// as CrossScopeTaskSearchService.ClassifyByBoardAsync does for the cross-scope locator — this is
	// the SAME per-board runtime resolution, mirrored locally rather than shared, since the two
	// callers otherwise have nothing else in common (one is single-project, the other fans out).
	static async Task<Func<TaskSearchHit, StatusKind?>> ClassifyByBoardAsync(
		ITasksService tasks, string projectKey, IReadOnlyList<TaskSearchHit> hits, CancellationToken ct)
	{
		var boards = hits.Select(h => h.Board).Distinct(StringComparer.Ordinal).ToList();
		if (boards.Count == 0) return _ => null;

		var kindByBoard = (await tasks.ListBoardsAsync(projectKey, ct))
			.ToDictionary(b => b.Name, b => b.Kind, StringComparer.Ordinal);
		var runtimeByBoard = new Dictionary<string, MethodologyRuntime>(StringComparer.Ordinal);
		foreach (var b in boards)
			runtimeByBoard[b] = await tasks.GetRuntimeForBoardAsync(projectKey, b, ct);

		return h => runtimeByBoard.TryGetValue(h.Board, out var rt)
			? rt.StatusKindOf(kindByBoard.GetValueOrDefault(h.Board), h.Node.Status)
			: null;
	}

	// The query identity this cursor is bound to — mirrors TasksTools.SearchAsync's own
	// SearchFingerprint (project-wide: no board/under/status/statusKind facet on this MVP screen).
	static string SearchFingerprint(string query, TaskSortBy axis, bool desc, string? dataVersion) =>
		KeysetCursor.FingerprintOf("project-tasks-search", query, axis.ToString(), desc ? "1" : "0", dataVersion);

	// Mirrors TasksTools.CursorSortValue exactly — see that method's own comment for why RELEVANCE
	// resumes by identity only (Advance tries that first) and never by comparing the score.
	static string CursorSortValue(TaskSearchHit h, TaskSortBy by) => by switch
	{
		TaskSortBy.Priority => h.Node.Priority.ToString(CultureInfo.InvariantCulture),
		TaskSortBy.Title => h.Node.Title,
		TaskSortBy.Created => (h.Node.CreatedAt ?? default).ToString("O", CultureInfo.InvariantCulture),
		TaskSortBy.Updated => (h.Node.UpdatedAt ?? default).ToString("O", CultureInfo.InvariantCulture),
		TaskSortBy.Relevance => (h.Score ?? 0).ToString("R", CultureInfo.InvariantCulture),
		_ => throw new ArgumentException($"project-tasks-search: sort axis '{by}' cannot carry a cursor"),
	};

	static Comparison<string> CursorSortComparison(TaskSortBy by) => by switch
	{
		TaskSortBy.Priority => static (a, b) => long.Parse(a, CultureInfo.InvariantCulture).CompareTo(long.Parse(b, CultureInfo.InvariantCulture)),
		TaskSortBy.Title => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a, b),
		TaskSortBy.Created or TaskSortBy.Updated => static (a, b) =>
			DateTime.Parse(a, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
				.CompareTo(DateTime.Parse(b, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
		TaskSortBy.Relevance => static (_, _) => throw new ArgumentException(
			"project-tasks-search: the row this cursor names is no longer in the ranked pool, and a relevance "
			+ "position cannot be re-derived from its score. Drop the cursor and start the search over."),
		_ => throw new ArgumentException($"project-tasks-search: sort axis '{by}' cannot carry a cursor"),
	};

	static TaskSortBy ParseSortBy(string sortBy) => sortBy switch
	{
		TaskSearchSortKeys.Priority => TaskSortBy.Priority,
		TaskSearchSortKeys.Created => TaskSortBy.Created,
		TaskSearchSortKeys.Updated => TaskSortBy.Updated,
		TaskSearchSortKeys.Title => TaskSortBy.Title,
		_ => TaskSortBy.Relevance,
	};

	// Surfaced only on Stop == "pool-boundary" — the human-readable twin of tasks_search's own
	// PoolBoundaryHint: don't read this as "that was everything", there is no further page to fetch.
	const string PoolBoundaryHintText =
		"Ranking depth reached: more tasks matched this search than relevance ranking looked at, so this is a "
		+ "prefix of the match set, not all of it — and there is no further page to fetch, because the rest was "
		+ "never ranked. Narrow the search (a more specific query) to reach it.";
}

// The sort-key vocabulary this page's form and OnGetAsync both switch over — mirrors
// SessionSortKeys/the tasks_search axis vocabulary. Kept as one named list so an
// unrecognized/typo'd `sortBy` degrades to the default (relevance) instead of throwing.
public static class TaskSearchSortKeys
{
	public const string Relevance = "relevance";
	public const string Priority = "priority";
	public const string Created = "created";
	public const string Updated = "updated";
	public const string Title = "title";

	public static readonly IReadOnlyList<string> All = [Relevance, Priority, Created, Updated, Title];

	public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.OrdinalIgnoreCase);
}
