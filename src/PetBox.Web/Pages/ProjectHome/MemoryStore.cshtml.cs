using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Search;
using PetBox.Memory.Contract;
using PetBox.Web.Auth;
using PetBox.Web.Memory;
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.ProjectHome;

// Detail for one memory store (/ui/{ws}/{project}/memory/{store}).
//
// With no `q`, the LISTING mode (spec listing-tail-reachable): the full deterministic order
// (Updated desc by default, then Key, then Store — MemoryService.SortSelected) comes back from
// SearchEntriesAsync(Query: null, Limit: 0) unbounded, exactly like TasksTools.SearchAsync's own
// listing mode; THIS adapter seeks a KeysetCursor through it and slices one page — no offset, no
// pageNum. An offset ("skip the first 40") is wrong under concurrent writes: an insert before the
// boundary silently re-serves a row, a delete before it silently swallows one, and neither is
// visible to the caller (KeysetCursor.cs:17-23). The cursor's Fingerprint hashes in everything
// that decides the listing's selection/order (scope, type, store, sort axis+direction) — change
// any of them mid-walk and the OLD cursor is refused with an instructive error, never silently
// restarted against a different ordering.
//
// A non-empty `q` runs the SAME hybrid engine MCP's memory_search calls
// (IMemoryService.SearchEntriesAsync) instead of the old substring LIKE — filtered by type, scoped
// project/workspace/cascade, sorted relevance/created/updated, each hit carrying its fused score
// and retriever. QUERY mode PAGES TOO (spec: result-set-pageable overturned the old "q carries no
// cursor" doctrine): the ranked pool is materialized once (WholePool: true) and this adapter seeks a
// KeysetCursor through it exactly as the listing branch does — the fused relevance order is a FINITE,
// TOTALLY ORDERED pool, so a query is a filter like any other, not a reason to refuse navigation.
// `Stop` always accompanies a query page ("more" | "exhausted" | "pool-boundary") so the human surface
// never has to infer the end from a missing cursor — the same three-way answer memory_search reports.
// Exactly the same split TasksTools.SearchAsync draws between listing and query mode, now unified on
// pageability rather than on which one pages.
//
// Existence is checked against metadata first so we don't auto-vivify a phantom file.
// WorkspaceViewer + project↔route workspace bind — same tenant gate as Memory page.
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class MemoryStoreModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly IMemoryService _memory;
	// Optional ctor param, same posture as TaskBoardNodeModel's _catalog: needed only to gate
	// MemorySearchScope's derived workspace-container leg (SandboxContainment.PermitsAsync) — DI
	// always supplies it (IProjectCatalog is registered unconditionally), a bare unit-test
	// construction that never exercises a workspace/cascade search may omit it.
	readonly IProjectCatalog? _catalog;
	// Optional, same posture as _catalog: resolves the caller's ui-search-ranking-mode-preference
	// override (BrowserState.SearchRankingMode) of the UI edge default. DI always supplies it
	// (IUiState is registered unconditionally); a bare unit-test construction with no HttpContext
	// may omit it and falls back to the pre-existing hardcoded Speed default below.
	readonly IUiState? _uiState;

	public MemoryStoreModel(IProjectDirectory projects, FeatureFlags features, IMemoryService memory,
		IProjectCatalog? catalog = null, IUiState? uiState = null)
	{
		_projects = projects;
		_features = features;
		_memory = memory;
		_catalog = catalog;
		_uiState = uiState;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "store")]
	public string Store { get; set; } = string.Empty;

	// The page's resume token (spec listing-tail-reachable / result-set-pageable) — a
	// KeysetCursor.Encode() string, opaque by contract. Used in BOTH modes now: a listing cursor seeks
	// the deterministic order, a query cursor seeks the materialized ranked pool (WholePool) — each
	// mode builds its own fingerprint, so a token from one is refused (not silently honoured) against
	// the other.
	[BindProperty(SupportsGet = true, Name = "cursor")]
	public string? Cursor { get; set; }

	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Query { get; set; }

	// Taxonomy filter (User|Feedback|Project|Reference) — now applies in BOTH modes (a listing
	// used to ignore it silently; that inconsistency is closed as part of the fingerprint fix
	// below, since a filter that changed the SET without invalidating the cursor would be exactly
	// the "silent restart" the type exists to prevent).
	[BindProperty(SupportsGet = true, Name = "type")]
	public string? Type { get; set; }

	// project (default) | workspace | cascade — which container(s) are read, mirroring
	// memory_search's `scope`. Applies in both modes.
	[BindProperty(SupportsGet = true, Name = "scope")]
	public string? Scope { get; set; }

	// relevance (search default) | created | updated (listing default) — reorders WITHIN the
	// selected set, same as memory_search's `sort`. In LISTING mode "relevance" has no meaning (no
	// relevance leg runs) and resolves to the service's own default (Updated desc).
	[BindProperty(SupportsGet = true, Name = "sort")]
	public string? Sort { get; set; }

	// ui-search-page-position-and-size: page size is a control now, not the bare PageSize=40
	// constant it used to be. A stale/hand-edited value degrades to PageSizeOptions.Default rather
	// than erroring (EffectiveSize below). Deliberately NOT part of any cursor fingerprint
	// (KeysetCursor.cs:38-40 — it shapes the page, never the sequence) — living in the FILTER form
	// (a plain GET, no cursor/pos carried) is what makes changing it start the walk over.
	[BindProperty(SupportsGet = true, Name = "size")]
	public int? Size { get; set; }
	public int EffectiveSize => PageSizeOptions.Resolve(Size);

	// ui-search-page-position-and-size: rows already delivered BEFORE this page — a plain
	// presentation counter, NOT part of the keyset cursor (the cursor is a position in the ORDER;
	// this is a position in the WALK, and the two must not be conflated — mixing a presentational
	// counter into a token whose bytes are checked for integrity (fingerprint/order-hash) would
	// make an unrelated concern able to invalidate it). Carried as its own query param, propagated
	// by the Next link, defaulting to 0 (the first page) when absent/negative.
	[BindProperty(SupportsGet = true, Name = "pos")]
	public int Pos { get; set; }
	int EffectivePos => Pos < 0 ? 0 : Pos;

	// The inclusive 1-based range of rows THIS page shows (spec result-set-pageable: "what range of
	// rows is shown", never an invented total). Set once, after the branch below knows how many rows
	// actually landed on this page — 0 rows renders neither (the empty-state alert covers that case).
	public int RangeFrom { get; private set; }
	public int RangeTo { get; private set; }

	// The scope control is moot when this project IS already the workspace's shared-memory
	// container (MemorySearchScope.ResolveContainersAsync collapses to one leg regardless) — hide
	// it rather than offer a choice with no effect.
	public bool ScopeSelectable => MemorySearchScope.IsScopeSelectable(WorkspaceKey, ProjectKey);

	// The deep-link half of the stable entry URL (…/memory/{store}?key={key}#{key}, MemoryLinks):
	// the SERVER resolves the entry's position in the canonical listing and seeds a cursor that
	// makes it the FIRST row of the page it renders, so the fragment always has a card to land on.
	// A bare fragment cannot do this alone: it is never sent to the server, so the entry was
	// silently absent from the DOM for every store bigger than one page.
	[BindProperty(SupportsGet = true, Name = MemoryLinks.KeyParam)]
	public string? Key { get; set; }

	// The key the request asked for AND that this page actually renders — the card is marked
	// `data-highlight="true"` so the highlight does not hang on `:target` alone.
	public string? HighlightKey { get; private set; }

	// True once a non-empty (trimmed) `q` is in play — the page renders Hits instead of Entries.
	public bool IsSearch { get; private set; }

	// LISTING rows (IsSearch false) — Score is always 0 / Retriever always null (no relevance leg
	// ran); the razor view hides those badges for this branch.
	public IReadOnlyList<MemorySearchScope.Row> Entries { get; private set; } = [];
	// SearchEntriesAsync hits (IsSearch only) — each carries its owning Scope/Store, the fused
	// Score and the Retriever ("lexical"/"semantic"), same provenance memory_search returns.
	public IReadOnlyList<MemorySearchScope.Row> Hits { get; private set; } = [];
	// Retriever provenance for the search (null outside IsSearch — no relevance leg ran).
	public SearchRetrievers? Retrievers { get; private set; }
	public int Total { get; private set; }
	// Whether more rows exist past this page, in BOTH modes now.
	public bool HasNext { get; private set; }
	// The token for the "next" link when HasNext — null means this IS the last page.
	public string? NextCursor { get; private set; }
	// A malformed/stale `?cursor=` (a different query/filter/sort since it was issued, or garbage) is
	// a LOUD refusal (KeysetCursor's own contract), never a silent restart — this carries that message
	// so the page can say so and still render page 1 rather than 500ing. Shared by both modes.
	public string? CursorError { get; private set; }
	// QUERY mode only (spec: result-set-pageable card requirement 2) — WHY the walk stopped, stated
	// rather than implied: "more" | "exhausted" | "pool-boundary". Null in listing mode (there is no
	// ranking depth to run out of). "pool-boundary" means ranking looked only PoolBoundaryHint's
	// declared depth deep and more entries matched behind it — the rest was never ranked, so there is
	// no further page to reach; the way forward is to narrow the query, not to keep clicking Next.
	public string? Stop { get; private set; }
	// Surfaced only when Stop == "pool-boundary" — the human-readable version of the same fact
	// pool-boundary-hint gives an agent: don't read this as "that was everything".
	public string? PoolBoundaryHint { get; private set; }

	// Usage counters (spec: memory-usage-observability), keyed by MemorySearchScope.UsageKey(scope,
	// store, key) — shared by both modes now that listing can span scope too. Viewing this page is
	// curation, not usage — it reads the counters and never increments them.
	public IReadOnlyDictionary<string, MemoryUsageView> HitUsage { get; private set; } =
		new Dictionary<string, MemoryUsageView>();

	// Store-wide usage aggregate (spec: memory-usage-aggregate) — rendered as a summary
	// band above the entries, in BOTH modes (it describes the store, not the current query).
	// Reading this page is curation, never an impression.
	public MemoryUsageAggregate? Aggregate { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Memory)) return NotFound();
		// "Not there" and "not yours" are the same 404: the workspace is part of the lookup, so the
		// route cannot be used to probe for another tenant's project.
		var project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (project is null) return NotFound();
		if (!await _memory.StoreExistsAsync(ProjectKey, Store, ct)) return NotFound();

		// A `?key=` deep-link OWNS the view: drop every other narrowing (q/type/scope/sort/cursor)
		// so the entry is resolved against the CANONICAL listing, then seed a synthetic cursor that
		// makes it the page's first row. Dropping the narrowing here is deliberate — the entry the
		// link names must not hide behind a stale filter carried in from wherever the link was
		// copied; a key that no longer resolves (deleted entry, typo) leaves the canonical listing
		// as-is and simply highlights nothing.
		if (!string.IsNullOrWhiteSpace(Key))
		{
			Query = null; Type = null; Scope = null; Sort = null; Cursor = null;
			var (canonical, axis, _, fingerprint) = await LoadListingAsync(ct);
			var idx = canonical.Select(r => r.Entry.Key).ToList().FindIndex(k => string.Equals(k, Key, StringComparison.Ordinal));
			if (idx >= 0)
			{
				HighlightKey = Key;
				if (idx > 0)
				{
					var before = canonical[idx - 1];
					Cursor = new KeysetCursor(fingerprint, CursorSortValue(before, axis), before.Entry.Key, before.Store).Encode();
				}
			}
		}

		var q = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim();
		IsSearch = q is not null;
		if (IsSearch)
		{
			var searchSort = ParseSearchSortBy(Sort);
			// EDGE default (spec: search-ranking-mode-is-caller-choice) — Speed, because a human
			// skimming a page pays more for latency than for a ranking mistake; the MCP surface takes
			// the opposite default for the opposite reason. ui-search-ranking-mode-preference: this
			// EDGE default is now overridable per-user (BrowserState.SearchRankingMode,
			// /ui/me/preferences) — no longer a bare constant. _uiState null (bare unit-test
			// construction, no HttpContext) falls back to the same Speed the constant used to be.
			var rankingMode = _uiState is not null ? (await _uiState.GetAsync(ct)).SearchRankingMode : PetBox.Core.Search.SearchRankingMode.Speed;
			var result = await MemorySearchScope.SearchAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope,
				new SearchRequest<MemoryEntryFilter, MemorySortBy>
				{
					Query = q,
					Filter = new MemoryEntryFilter(Store, Type),
					Sort = searchSort,
					Limit = EffectiveSize,
					RankingMode = rankingMode,
					BodyLen = 0, // full bodies — the page already rendered full bodies in listing mode
								 // PAGING (spec: result-set-pageable) — materialize the WHOLE ranked pool instead of a
								 // bare top-K so this adapter can seek a KeysetCursor through it, mirroring the listing
								 // branch below and memory_search's own MCP cascade.
					WholePool = true,
				}, ct);
			Retrievers = result.Retrievers;

			var fingerprint = SearchFingerprint(q, searchSort, result.DataVersion);
			var afterCursor = result.Rows;
			if (!string.IsNullOrWhiteSpace(Cursor))
			{
				try
				{
					var decoded = KeysetCursor.Decode(Cursor, fingerprint, "memory-store-search");
					// THE ORDER COMMITMENT (spec: result-set-pageable) — the fingerprint only proves the
					// QUESTION is unchanged; this proves the ranked ANSWER is still in the sequence the
					// token was issued against (a rerank route recovering/failing between pages reorders
					// the same rows with nothing written). Checked before the seek, same as memory_search.
					if (!string.IsNullOrEmpty(result.PoolOrderHash))
						decoded.AssertPoolOrder(result.PoolOrderHash, "memory-store-search");
					afterCursor = KeysetCursor.Advance(
						result.Rows, decoded, r => ("", r.Store + "\x1f" + r.Entry.Key, r.Scope),
						SearchCursorComparison, desc: false, "memory-store-search");
				}
				catch (ArgumentException ex)
				{
					// Loud, not silent — same posture as the listing branch: render page 1 rather than 500.
					CursorError = ex.Message;
					afterCursor = result.Rows;
				}
			}

			Hits = afterCursor.Take(EffectiveSize).ToList();
			Total = Hits.Count;
			HasNext = afterCursor.Count > EffectiveSize;
			if (HasNext)
			{
				var last = Hits[^1];
				NextCursor = new KeysetCursor(fingerprint, "", last.Store + "\x1f" + last.Entry.Key, last.Scope,
					result.PoolOrderHash ?? "").Encode();
			}
			// WHY THE WALK STOPPED — stated, not implied (card requirement 2). Never infer the end from a
			// missing cursor: "exhausted" and "pool-boundary" both omit it and mean different things.
			Stop = HasNext ? "more" : result.PoolBounded ? "pool-boundary" : "exhausted";
			// ui-search-page-position-and-size: the range of rows THIS page shows — never an invented
			// total (the ranked pool's true match count is unknowable mid-walk; poolLimit is ranking
			// DEPTH, not a match count). 0 rows leaves both at 0 — the empty-state alert covers that.
			if (Hits.Count > 0) { RangeFrom = EffectivePos + 1; RangeTo = EffectivePos + Hits.Count; }
			PoolBoundaryHint = Stop == "pool-boundary" ? PoolBoundaryHintText : null;
			HitUsage = await MemorySearchScope.LoadUsageAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope, Hits, ct);
		}
		else
		{
			var (ordered, axis, desc, fingerprint) = await LoadListingAsync(ct);
			var afterCursor = ordered;
			if (!string.IsNullOrWhiteSpace(Cursor))
			{
				try
				{
					var decoded = KeysetCursor.Decode(Cursor, fingerprint, "memory-store");
					afterCursor = KeysetCursor.Advance(
						ordered, decoded, r => (CursorSortValue(r, axis), r.Entry.Key, r.Store),
						CursorSortComparison(axis), desc, "memory-store");
				}
				catch (ArgumentException ex)
				{
					// Loud, not silent: the type's whole point is refusing to splice two orderings
					// together unannounced. The page still renders — from the top — rather than 500ing.
					CursorError = ex.Message;
				}
			}

			Total = ordered.Count;
			Entries = afterCursor.Take(EffectiveSize).ToList();
			HasNext = afterCursor.Count > EffectiveSize;
			if (HasNext)
			{
				var last = Entries[^1];
				NextCursor = new KeysetCursor(fingerprint, CursorSortValue(last, axis), last.Entry.Key, last.Store).Encode();
			}
			// ui-search-page-position-and-size: same range presentation as the search branch — here
			// Total is a REAL count (the full deterministic listing), so this range is "of Total", not
			// a prefix-of-unknown.
			if (Entries.Count > 0) { RangeFrom = EffectivePos + 1; RangeTo = EffectivePos + Entries.Count; }
			HitUsage = await MemorySearchScope.LoadUsageAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope, Entries, ct);
		}
		Aggregate = await _memory.GetUsageAggregateAsync(ProjectKey, Store, ct: ct);
		return Page();
	}

	// The full deterministic listing order for the CURRENT Type/Scope/Sort — unbounded (Limit: 0),
	// exactly like TasksTools.SearchAsync's own listing mode. The caller (OnGetAsync) seeks a
	// cursor through it and slices a page; this method never truncates.
	async Task<(IReadOnlyList<MemorySearchScope.Row> Ordered, MemorySortBy Axis, bool Desc, string Fingerprint)> LoadListingAsync(CancellationToken ct)
	{
		var (axis, desc) = ParseListingSortBy(Sort);
		var result = await MemorySearchScope.SearchAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope,
			new SearchRequest<MemoryEntryFilter, MemorySortBy>
			{
				Query = null,
				Filter = new MemoryEntryFilter(Store, Type),
				Sort = (axis, desc),
				Limit = 0, // unbounded listing — the adapter is the one that seeks/slices
				BodyLen = 0,
			}, ct);
		return (result.Rows, axis, desc, ListingFingerprint(axis, desc));
	}

	// Everything that decides the LISTING's selection + order, hashed into the cursor (spec
	// listing-tail-reachable / KeysetCursor's own FINGERPRINT contract): workspace/project/store
	// pin the container(s) actually read, scope/type narrow the pool, axis+desc name the order. A
	// caller that pages through a filter/sort change gets a loud "different query" error, never a
	// silently spliced page.
	string ListingFingerprint(MemorySortBy axis, bool desc) => KeysetCursor.FingerprintOf(
		WorkspaceKey, ProjectKey, Store, NormalizeScope(Scope), NormalizeType(Type),
		axis.ToString(), desc.ToString());

	static string NormalizeScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
	{
		"workspace" => "workspace",
		"cascade" => "cascade",
		_ => "project",
	};

	static string NormalizeType(string? type) => string.IsNullOrWhiteSpace(type) ? "" : type.Trim().ToLowerInvariant();

	// Everything that decides the QUERY's selection + order, hashed into the cursor — mirrors
	// ListingFingerprint's job for the search branch. `dataVersion` (the joined per-container stamps
	// MemorySearchScope returns) pins the token to the exact store state the pool was ranked over: edit
	// the store mid-walk and the next page is REFUSED with an instructive error, never silently
	// restarted against a new ordering (spec: result-set-pageable card requirement 4).
	string SearchFingerprint(string? q, (MemorySortBy By, bool Desc)? sort, string? dataVersion) =>
		KeysetCursor.FingerprintOf(
			"memory-store-search", WorkspaceKey, ProjectKey, Store, NormalizeScope(Scope), NormalizeType(Type), q,
			sort?.By.ToString(), sort?.Desc.ToString(), dataVersion);

	// The relevance order has no scalar that means anything (fused score is freshness/decay-blended in
	// cascade scope, exact-identity rows carry none) — resumption is by IDENTITY only (KeysetCursor.Advance
	// tries that first). This delegate is reached only when the boundary row is gone from the pool, which
	// the data-version + order-hash pinning in the fingerprint/AssertPoolOrder should already have refused;
	// if it somehow is reached, refuse explicitly rather than guess a boundary from a value that doesn't
	// order the list — the same posture memory_search's own CursorSortComparison takes.
	static Comparison<string> SearchCursorComparison => (_, _) => throw new ArgumentException(
		"memory-store: the row this cursor names is no longer in the ranked pool, and a relevance position "
		+ "cannot be re-derived from it (the order is score-fused and, in cascade scope, freshness-blended). "
		+ "Drop the cursor and start the search over.");

	// Surfaced only on Stop == "pool-boundary" — the human-readable twin of memory_search's
	// PoolBoundaryHint: don't read this as "that was everything", there is no further page to fetch.
	const string PoolBoundaryHintText =
		"Ranking depth reached: more entries matched this search than relevance ranking looked at, so this "
		+ "is a prefix of the match set, not all of it — and there is no further page to fetch, because the "
		+ "rest was never ranked. Narrow the search (type, scope, a more specific query) to reach it.";

	// LISTING mode always resolves to a CONCRETE axis (never null/relevance — there is no
	// relevance leg to default to) so the fingerprint captures what's actually in effect: an
	// unspecified sort and an explicit `sort=updated` must hash the SAME, since they produce the
	// identical order. Mirrors TasksTools.SearchAsync's `parsedSort?.By ?? TaskSortBy.Priority`.
	static (MemorySortBy By, bool Desc) ParseListingSortBy(string? sort) => sort?.Trim().ToLowerInvariant() switch
	{
		"created" => (MemorySortBy.Created, true),
		"updated" => (MemorySortBy.Updated, true),
		_ => (MemorySortBy.Updated, true), // the service's own listing default — freshest fact first
	};

	// SEARCH mode maps the `sort` query arg onto the service sort axis. null/empty/"relevance" →
	// null (the service's own default: the fused relevance order — sorting BY relevance is only
	// valid implicitly, MemoryService rejects an explicit Sort.By==Relevance combined with no
	// query, and this path always has one). created/updated default to DESC (newest first).
	static (MemorySortBy By, bool Desc)? ParseSearchSortBy(string? sort) => sort?.Trim().ToLowerInvariant() switch
	{
		"created" => (MemorySortBy.Created, true),
		"updated" => (MemorySortBy.Updated, true),
		_ => null,
	};

	// The cursor's sort-key value for one LISTING row, on the axis actually in effect. Mirrors
	// TasksTools.CursorSortValue — Created/Updated are the only listing axes (relevance never
	// reaches here: a listing cannot sort by it).
	static string CursorSortValue(MemorySearchScope.Row row, MemorySortBy axis) => axis switch
	{
		MemorySortBy.Created => row.Created.ToString("O", CultureInfo.InvariantCulture),
		MemorySortBy.Updated => row.Updated.ToString("O", CultureInfo.InvariantCulture),
		_ => throw new ArgumentException($"memory-store: sort axis '{axis}' cannot carry a listing cursor"),
	};

	// How two of those canonical values compare — instants, not text, matching how the service
	// itself ordered them (MemoryService.SortSelected/Ordered). NOTE (cascade + comparison
	// fallback): KeysetCursor.Advance tries IDENTITY first (resume right after the previously-seen
	// row, wherever it now sits) and only falls back to this comparison when that row is gone
	// (deleted between page loads). In scope=cascade listing the true order is (container rank,
	// then Updated/Created) — the container boundary is NOT encoded here, so the fallback alone can
	// misplace a row by at most the cascade boundary in that rare case. This is the same class of
	// "known, accepted anomaly" KeysetCursor.cs already documents for an edited sort key — fixing
	// it needs an as-of snapshot of both containers, out of proportion to the anomaly.
	static Comparison<string> CursorSortComparison(MemorySortBy axis) => axis switch
	{
		MemorySortBy.Created or MemorySortBy.Updated => static (a, b) =>
			DateTime.Parse(a, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
				.CompareTo(DateTime.Parse(b, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
		_ => throw new ArgumentException($"memory-store: sort axis '{axis}' cannot carry a listing cursor"),
	};
}
