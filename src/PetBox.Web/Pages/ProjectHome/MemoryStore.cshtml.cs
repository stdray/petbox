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
// and retriever. QUERY mode carries NO cursor (spec listing-tail-reachable's own boundary): the
// fused relevance order is recomputed per call over a bounded candidate pool, so there is no tail
// behind it to page into — a selection, not an enumeration. Exactly the same split
// TasksTools.SearchAsync draws between listing and query mode.
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

	public MemoryStoreModel(IProjectDirectory projects, FeatureFlags features, IMemoryService memory, IProjectCatalog? catalog = null)
	{
		_projects = projects;
		_features = features;
		_memory = memory;
		_catalog = catalog;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "store")]
	public string Store { get; set; } = string.Empty;

	// The listing's resume token (spec listing-tail-reachable) — a KeysetCursor.Encode() string,
	// opaque by contract. Ignored in search mode (there is no cursor to carry there).
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

	const int PageSize = 40;

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
	// LISTING mode only: whether more rows exist past this page. Always false in search mode —
	// SearchEntriesAsync caps a query by Limit, it does not page: a search result is a bounded
	// top-N selection, not a walkable list (same shape memory_search returns to an agent).
	public bool HasNext { get; private set; }
	// The token for the "next" link when HasNext — null means this IS the last page.
	public string? NextCursor { get; private set; }
	// A malformed/stale `?cursor=` (a different query since it was issued, or garbage) is a LOUD
	// refusal (KeysetCursor's own contract), never a silent restart — this carries that message so
	// the page can say so and still render page 1 rather than 500ing.
	public string? CursorError { get; private set; }

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
			var result = await MemorySearchScope.SearchAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope,
				new SearchRequest<MemoryEntryFilter, MemorySortBy>
				{
					Query = q,
					Filter = new MemoryEntryFilter(Store, Type),
					Sort = ParseSearchSortBy(Sort),
					Limit = PageSize,
					// EDGE default (spec: search-ranking-mode-is-caller-choice) — a human skimming a
					// page, where latency costs more than a ranking mistake. Precision here would put
					// the measured 3-4s cross-encoder in front of a person for every keystroke's worth
					// of query; the MCP surface takes the opposite default for the opposite reason.
					RankingMode = PetBox.Core.Search.SearchRankingMode.Speed,
					BodyLen = 0, // full bodies — the page already rendered full bodies in listing mode
				}, ct);
			Hits = result.Rows;
			Retrievers = result.Retrievers;
			Total = Hits.Count;
			HasNext = false;
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
			Entries = afterCursor.Take(PageSize).ToList();
			HasNext = afterCursor.Count > PageSize;
			if (HasNext)
			{
				var last = Entries[^1];
				NextCursor = new KeysetCursor(fingerprint, CursorSortValue(last, axis), last.Entry.Key, last.Store).Encode();
			}
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
