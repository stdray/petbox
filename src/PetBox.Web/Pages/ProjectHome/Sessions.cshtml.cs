using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Web.Auth;
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI sessions list for a project (/ui/{ws}/{project}/sessions). There is no catalog: one
// sessions file per project, written by agents via the session MCP tools.
// Gated on Feature.Tasks (sessions ship with the Tasks module).
// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class SessionsModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly ISessionStore _store;
	readonly SessionSearchService _search;
	// Optional, same posture as MemoryModel._uiState: resolves the caller's
	// ui-search-ranking-mode-preference override (BrowserState.SearchRankingMode) of the UI edge
	// default (Speed) — this NOW reaches session search too (search-rerank-for-sessions), on BOTH
	// its discovery and episodic-hydration legs. DI always supplies it (IUiState is registered
	// unconditionally); a bare unit-test construction with no HttpContext may omit it and falls
	// back to the pre-existing hardcoded Speed default below.
	readonly IUiState? _uiState;

	public SessionsModel(IProjectDirectory projects, FeatureFlags features, ISessionStore store, SessionSearchService search,
		IUiState? uiState = null)
	{
		_projects = projects;
		_features = features;
		_store = store;
		_search = search;
		_uiState = uiState;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	// PAGINATION in BOTH modes now (card listing-keyset-memory-sessions / spec result-set-pageable):
	// the opaque KeysetCursor token from the previous page's NextCursor, passed back verbatim.
	// Listing mode seeks the deterministic SQL order (ListPageAsync); search mode seeks the
	// discovery-order pool (SessionSearchService), the same engine session_search's own `cursor`
	// walks — the old "q carries no cursor" doctrine (spec listing-tail-reachable's original
	// boundary) is the one result-set-pageable overturned.
	[BindProperty(SupportsGet = true, Name = "cursor")]
	public string? Cursor { get; set; }

	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Query { get; set; }

	// Exact-match agent filter (a dropdown of the project's distinct agents) — a DIFFERENT
	// predicate from `q`'s free-text SessionId-or-Agent substring match, combinable with it.
	// Applies in BOTH listing and search mode (spec search-one-engine-for-human-and-agent: the
	// same filter set on both surfaces).
	[BindProperty(SupportsGet = true, Name = "agent")]
	public string? Agent { get; set; }

	// "updated" (default) | "created" | "length". Unrecognized/absent falls back to "updated"
	// (SessionSortKeys.IsKnown) rather than erroring — mirrors BoardSortKeys' tolerance of a
	// stale/typo'd value.
	[BindProperty(SupportsGet = true, Name = "sortBy")]
	public string? SortBy { get; set; }

	[BindProperty(SupportsGet = true, Name = "sortDesc")]
	public bool? SortDesc { get; set; }

	// ui-search-page-position-and-size: see MemoryStoreModel's identical pair for the full
	// rationale. SEARCH mode has its own hard ceiling underneath this (SessionSearchService.
	// MaxSessions=30 — a hydration-cost cap, not a UI choice), so the effective request is capped
	// at it; RangeTo is always derived from the ACTUAL rows returned, never the requested size, so
	// picking 100 here honestly renders "rows 1-30" rather than silently pretending to serve 100.
	[BindProperty(SupportsGet = true, Name = "size")]
	public int? Size { get; set; }
	public int EffectiveSize => PageSizeOptions.Resolve(Size);
	int EffectiveSearchSessions => Math.Min(EffectiveSize, SessionSearchService.MaxSessions);

	[BindProperty(SupportsGet = true, Name = "pos")]
	public int Pos { get; set; }
	int EffectivePos => Pos < 0 ? 0 : Pos;

	public int RangeFrom { get; private set; }
	public int RangeTo { get; private set; }

	public Project? Project { get; private set; }
	public bool SessionsEnabled => _features.IsEnabled(Feature.Tasks);

	// Populated in the LISTING path (no `q`) — a deterministic page of headers.
	public IReadOnlyList<SessionHeader> Sessions { get; private set; } = [];

	// Populated in the SEARCH path (`q` set) — the same two-stage engine session_search (MCP)
	// uses (digest ⊕ term ⊕ optional fullscan discovery, then episodic hydration with message
	// ordinals). PAGES now too (spec: result-set-pageable): the discovery pool is a finite,
	// totally-ordered walk, and `Stop`/`NextCursor` below carry the same three-way answer
	// session_search's own cursor reports.
	public IReadOnlyList<SessionSearchCandidate> SearchResults { get; private set; } = [];

	public bool IsSearchMode => !string.IsNullOrWhiteSpace(Query);

	// False = distillation (the digest store) hasn't reached this project yet — an honest
	// "index still warming up", not "nothing matched": the verbatim term leg is the declared
	// recall floor and still ran (SessionSearchOutcome.Distilled).
	public bool Distilled { get; private set; } = true;

	// Search mode: the candidate count (a bounded selection, not a corpus total). Listing mode:
	// the row count on THIS page — keyset paging has no cheap "total rows" (that would need an
	// extra COUNT the spec doesn't ask for); NextCursor is the only "more exists" signal.
	public int Total { get; private set; }

	// Listing mode only: the opaque token for the next page, or null at the tail. Always built
	// from THIS response's last row, so it can never itself be stale (see CursorWasReset for
	// the case where the INCOMING cursor was).
	public string? NextCursor { get; private set; }

	// Set when the incoming `cursor` was rejected — a fingerprint mismatch (stale link, or a
	// hand-edited URL mixing a cursor from one filter/sort with another), or (search mode only) the
	// session it named fell out of the discovery pool, or the pool was rebuilt in a different order
	// between pages. The page restarts from the top rather than surfacing a raw 500: the mismatch is
	// still CAUGHT (never silently spliced), just presented as a notice instead of a crash — a browser
	// page gets the LogCursor-style courtesy, tasks_search/session_search's MCP callers get the throw.
	// Shared by both modes now.
	public bool CursorWasReset { get; private set; }

	// SEARCH mode only (spec: result-set-pageable card requirement 2) — WHY the walk stopped, stated
	// rather than implied: "more" | "exhausted" | "pool-boundary". Null in listing mode (NextCursor
	// alone already answers "more exists" there — no ranking depth to run out of).
	public string? Stop { get; private set; }
	// Surfaced only when Stop == "pool-boundary" — don't read this as "that was everything".
	public string? PoolBoundaryHint { get; private set; }

	// The agent-filter dropdown's options — every distinct agent that has written a (non-
	// deleted) session in this project.
	public IReadOnlyList<string> Agents { get; private set; } = [];

	public string EffectiveSortBy => SessionSortKeys.IsKnown(SortBy) ? SortBy! : SessionSortKeys.Updated;
	public bool EffectiveSortDesc => SortDesc ?? true;

	public async Task OnGetAsync(CancellationToken ct)
	{
		// The route workspace is welded into the lookup — the second rubicon behind
		// ProjectWorkspaceBindingFilter, not a replacement for it (see ProjectHome/Index).
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !SessionsEnabled) return;

		// Header-only (no ContentZ decode — ISessionStore.ListAsync's own contract), used for
		// BOTH the agent-filter dropdown's options and (in search mode) as the Updated/
		// Created/Version lookup to reorder the candidate pool by a non-relevance axis (see
		// SortSearchResults below): SessionSearchCandidate itself carries no timestamps.
		var allHeaders = await _store.ListAsync(ProjectKey, ct);
		Agents = allHeaders.Select(h => h.Agent)
			.Where(a => !string.IsNullOrWhiteSpace(a))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (IsSearchMode)
		{
			// SEARCH: the SAME engine session_search (MCP) calls — spec
			// search-one-engine-for-human-and-agent forbids the human surface falling behind
			// the agent's on recall. PAGES too now (spec: result-set-pageable): the discovery
			// pool is a finite, totally-ordered walk, so `cursor` seeks into it exactly as
			// session_search's own `cursor` does — one PageSize slice at a time, not the whole
			// bounded pool in one shot.
			var fingerprint = SearchFingerprint(Query!, Agent);
			KeysetCursor? peeked = null;
			if (!string.IsNullOrWhiteSpace(Cursor))
			{
				try
				{
					var p = KeysetCursor.Peek(Cursor, "sessions-ui-search");
					p.AssertFingerprint(fingerprint, "sessions-ui-search");
					peeked = p;
				}
				catch (ArgumentException) { CursorWasReset = true; }
			}

			// ui-search-ranking-mode-preference: overridable per-user (BrowserState.SearchRankingMode,
			// UI edge default Speed) — session search now honours it on BOTH stages (search-rerank-for-sessions).
			var rankingMode = _uiState is not null ? (await _uiState.GetAsync(ct)).SearchRankingMode : SearchRankingMode.Speed;

			SessionSearchOutcome outcome;
			try
			{
				outcome = await _search.SearchAsync(ProjectKey, Query!, sessions: EffectiveSearchSessions, afterSessionId: peeked?.Key, mode: rankingMode, ct: ct);
			}
			catch (ArgumentException)
			{
				// The session this cursor named fell out of the discovery pool (SessionSearchService's
				// own refusal) — restart the walk from the top rather than a raw 500.
				CursorWasReset = true;
				outcome = await _search.SearchAsync(ProjectKey, Query!, sessions: EffectiveSearchSessions, mode: rankingMode, ct: ct);
			}
			if (peeked is { } token && !CursorWasReset)
			{
				try { token.AssertPoolOrder(outcome.DataVersion ?? "", "sessions-ui-search"); }
				catch (ArgumentException)
				{
					// THE ORDER COMMITMENT (spec: result-set-pageable): the pool was rebuilt and came back
					// ranked differently (a rerank/discovery input moved between pages) — restart rather
					// than splice two orderings.
					CursorWasReset = true;
					outcome = await _search.SearchAsync(ProjectKey, Query!, sessions: EffectiveSearchSessions, mode: rankingMode, ct: ct);
				}
			}
			Distilled = outcome.Distilled;

			// NextCursor/Stop are derived from THIS page's raw discovery order (outcome.Candidates),
			// before the Agent filter or a display sortBy reorders what's actually rendered — the same
			// "resume from the last row the walk considered" discipline session_search's adapter keeps.
			var more = outcome.MoreInPool;
			var resumeAfter = outcome.Candidates.Count > 0 ? outcome.Candidates[^1].SessionId : outcome.LastPoolKey;
			NextCursor = more && resumeAfter is not null
				? new KeysetCursor(fingerprint, "", resumeAfter, ProjectKey, outcome.DataVersion ?? "").Encode()
				: null;
			// WHY THE WALK STOPPED — the SAME three words tasks_search/memory_search/session_search use.
			Stop = more ? "more" : outcome.PoolBounded ? "pool-boundary" : "exhausted";
			PoolBoundaryHint = Stop == "pool-boundary" ? PoolBoundaryHintText : null;

			IEnumerable<SessionSearchCandidate> pool = outcome.Candidates;
			if (!string.IsNullOrWhiteSpace(Agent))
				pool = pool.Where(c => string.Equals(c.Agent, Agent, StringComparison.OrdinalIgnoreCase));

			// Relevance (the fused discovery order) is the default; an explicit sortBy REORDERS
			// the already-selected pool, it never widens it (the SearchRequest<,> convention:
			// "a filter narrows in both modes, sort reorders within the selected set") — and it
			// reorders only WITHIN this page, never the pool the cursor above walks.
			if (!string.IsNullOrWhiteSpace(SortBy))
			{
				var headerBySession = allHeaders.ToDictionary(h => h.SessionId, StringComparer.Ordinal);
				pool = SortSearchResults(pool, headerBySession, EffectiveSortBy, EffectiveSortDesc);
			}

			SearchResults = pool.ToList();
			Total = SearchResults.Count;
			// ui-search-page-position-and-size: derived from the ACTUAL row count, never the
			// requested size — honest even when EffectiveSearchSessions clamped the request.
			if (SearchResults.Count > 0) { RangeFrom = EffectivePos + 1; RangeTo = EffectivePos + SearchResults.Count; }
		}
		else
		{
			// LISTING: agent filter + sort are real SQL (ListPageAsync); paging is KEYSET, not
			// offset (card listing-keyset-memory-sessions) — a stale/foreign cursor is CAUGHT
			// (ArgumentException from KeysetCursor.Decode) and the listing restarts from the top
			// rather than raising a raw error to a browser tab.
			var sort = EffectiveSortBy switch
			{
				SessionSortKeys.Created => SessionSortField.Created,
				SessionSortKeys.Length => SessionSortField.Length,
				_ => SessionSortField.Updated,
			};
			SessionHeaderPage page;
			try
			{
				page = await _store.ListPageAsync(ProjectKey, null, Agent, sort, EffectiveSortDesc, Cursor, EffectiveSize, ct);
			}
			catch (ArgumentException)
			{
				CursorWasReset = true;
				Cursor = null;
				page = await _store.ListPageAsync(ProjectKey, null, Agent, sort, EffectiveSortDesc, null, EffectiveSize, ct);
			}
			Sessions = page.Headers;
			NextCursor = page.NextCursor;
			Total = Sessions.Count;
			if (Sessions.Count > 0) { RangeFrom = EffectivePos + 1; RangeTo = EffectivePos + Sessions.Count; }
		}
	}

	// Reorders search candidates by a non-relevance axis, looking up Updated/Created/Version
	// (the length proxy — see SessionSortField) from the header dict since
	// SessionSearchCandidate itself carries none of them. Missing lookups (should not happen —
	// every candidate came FROM this project's session set) sort last rather than throwing.
	static List<SessionSearchCandidate> SortSearchResults(IEnumerable<SessionSearchCandidate> pool,
		Dictionary<string, SessionHeader> headerBySession, string sortBy, bool desc)
	{
		DateTime UpdatedOf(SessionSearchCandidate c) => headerBySession.TryGetValue(c.SessionId, out var h) ? h.Updated : DateTime.MinValue;
		DateTime CreatedOf(SessionSearchCandidate c) => headerBySession.TryGetValue(c.SessionId, out var h) ? h.Created : DateTime.MinValue;
		long LengthOf(SessionSearchCandidate c) => headerBySession.TryGetValue(c.SessionId, out var h) ? h.Version : 0;

		IOrderedEnumerable<SessionSearchCandidate> ordered = sortBy switch
		{
			SessionSortKeys.Created => desc ? pool.OrderByDescending(CreatedOf) : pool.OrderBy(CreatedOf),
			SessionSortKeys.Length => desc ? pool.OrderByDescending(LengthOf) : pool.OrderBy(LengthOf),
			_ => desc ? pool.OrderByDescending(UpdatedOf) : pool.OrderBy(UpdatedOf),
		};
		return ordered.ToList();
	}

	// The query identity a search cursor is bound to: the discovery ORDER's underlying question. `Agent`
	// is included even though SessionSearchService never sees it (it's a UI-only post-filter over the
	// fetched page) — the human is still walking a selection scoped by it, and a value change mid-walk
	// must invalidate the token same as any other narrowing would. `sortBy`/`sortDesc` are deliberately
	// EXCLUDED: they reorder only the rendered page, never the underlying discovery walk the cursor
	// seeks (same "shapes the page, not the sequence" rule bodyLen/limit get elsewhere).
	string SearchFingerprint(string query, string? agent) =>
		KeysetCursor.FingerprintOf("sessions-ui-search", ProjectKey, query, agent);

	// Surfaced only on Stop == "pool-boundary" — the human-readable twin of session_search's own
	// PoolBoundaryHint: don't read this as "that was everything", there is no further page to fetch.
	const string PoolBoundaryHintText =
		"Discovery depth reached: more sessions were discovered than the pool walks, so this is a prefix "
		+ "of what matched, not all of it — and there is no further page to fetch. Narrow the search "
		+ "(a more specific query, or the agent filter) to reach it.";
}

// The sort-key vocabulary this page's form and OnGetAsync both switch over — kept as one named
// list (mirrors BoardSortKeys) so an unrecognized/typo'd `sortBy` degrades to the default
// instead of throwing, and the `<select>` options can't silently drift from the C# switch.
public static class SessionSortKeys
{
	public const string Updated = "updated";
	public const string Created = "created";
	public const string Length = "length";

	static readonly IReadOnlyList<string> All = [Updated, Created, Length];

	public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.OrdinalIgnoreCase);
}
