using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Web.Auth;
using PetBox.Web.Search;

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

	public SessionsModel(IProjectDirectory projects, FeatureFlags features, ISessionStore store, SessionSearchService search)
	{
		_projects = projects;
		_features = features;
		_store = store;
		_search = search;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	// The paging arg is 'pageNum', not 'page' — 'page' is a reserved route-key in Razor
	// Pages, so a ?page=N value never binds (see the Data-module table view lesson).
	[BindProperty(SupportsGet = true, Name = "pageNum")]
	public int PageNum { get; set; }

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

	const int PageSize = 30;

	public Project? Project { get; private set; }
	public bool SessionsEnabled => _features.IsEnabled(Feature.Tasks);

	// Populated in the LISTING path (no `q`) — a deterministic page of headers.
	public IReadOnlyList<SessionHeader> Sessions { get; private set; } = [];

	// Populated in the SEARCH path (`q` set) — the same two-stage engine session_search (MCP)
	// uses (digest ⊕ term ⊕ optional fullscan discovery, then episodic hydration with message
	// ordinals). This is a top-K relevance SELECTION, not an enumeration (mirrors the MCP
	// contract: "q is a relevance selection over discovered sessions, not an enumeration") — so
	// unlike the listing path there is no further page beyond the capped pool.
	public IReadOnlyList<SessionSearchCandidate> SearchResults { get; private set; } = [];

	public bool IsSearchMode => !string.IsNullOrWhiteSpace(Query);

	// False = distillation (the digest store) hasn't reached this project yet — an honest
	// "index still warming up", not "nothing matched": the verbatim term leg is the declared
	// recall floor and still ran (SessionSearchOutcome.Distilled).
	public bool Distilled { get; private set; } = true;

	public int Total { get; private set; }
	public bool HasNext { get; private set; }

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

		if (PageNum < 0) PageNum = 0;

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
			// the agent's on recall. Requesting a full PageSize pool (capped at
			// SessionSearchService.MaxSessions internally) — there is no `pageNum` beyond it.
			var outcome = await _search.SearchAsync(ProjectKey, Query!, sessions: PageSize, ct: ct);
			Distilled = outcome.Distilled;

			IEnumerable<SessionSearchCandidate> pool = outcome.Candidates;
			if (!string.IsNullOrWhiteSpace(Agent))
				pool = pool.Where(c => string.Equals(c.Agent, Agent, StringComparison.OrdinalIgnoreCase));

			// Relevance (the fused discovery order) is the default; an explicit sortBy REORDERS
			// the already-selected pool, it never widens it (the SearchRequest<,> convention:
			// "a filter narrows in both modes, sort reorders within the selected set").
			if (!string.IsNullOrWhiteSpace(SortBy))
			{
				var headerBySession = allHeaders.ToDictionary(h => h.SessionId, StringComparer.Ordinal);
				pool = SortSearchResults(pool, headerBySession, EffectiveSortBy, EffectiveSortDesc);
			}

			SearchResults = pool.ToList();
			Total = SearchResults.Count;
			HasNext = false;
		}
		else
		{
			// LISTING: agent filter + sort are real SQL (ListPageAsync), never an in-memory pass
			// over the whole set.
			var sort = EffectiveSortBy switch
			{
				SessionSortKeys.Created => SessionSortField.Created,
				SessionSortKeys.Length => SessionSortField.Length,
				_ => SessionSortField.Updated,
			};
			var page = await _store.ListPageAsync(ProjectKey, null, PageNum, PageSize, Agent, sort, EffectiveSortDesc, ct);
			Sessions = page.Headers;
			HasNext = page.HasNext;
			Total = page.Total;
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
}

// The sort-key vocabulary this page's form and OnGetAsync both switch over — kept as one named
// list (mirrors BoardSortKeys) so an unrecognized/typo'd `sortBy` degrades to the default
// instead of throwing, and the `<select>` options can't silently drift from the C# switch.
public static class SessionSortKeys
{
	public const string Updated = "updated";
	public const string Created = "created";
	public const string Length = "length";

	public static readonly IReadOnlyList<string> All = [Updated, Created, Length];

	public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.OrdinalIgnoreCase);
}
