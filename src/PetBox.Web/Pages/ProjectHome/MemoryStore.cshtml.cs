using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Search;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Web.Auth;
using PetBox.Web.Memory;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one memory store (/ui/{ws}/{project}/memory/{store}). With no `q`, shows
// the currently-active entries (ActiveTo == null) ordered by Key, offset-paginated — unchanged
// (spec search-one-engine-for-human-and-agent: "list = search without q" is a CONTRACT, not a
// mandate to route the deterministic listing through the same code path as a query; the offset
// pagination this listing depends on for the 200+-entry stores has no equivalent in
// SearchEntriesAsync, which caps a listing by Limit alone). A non-empty `q` now runs the SAME
// hybrid engine MCP's memory_search calls (IMemoryService.SearchEntriesAsync) instead of the old
// substring LIKE — filtered by type, scoped project/workspace/cascade, sorted
// relevance/created/updated, each hit carrying its fused score and retriever. Existence is
// checked against metadata first so we don't auto-vivify a phantom file.
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

	// The paging arg is 'pageNum', not 'page' — 'page' is a reserved route-key in Razor
	// Pages, so a ?page=N value never binds (see the Data-module table view lesson).
	// Only meaningful for the no-query listing — a search selection has no offset pages (see IsSearch).
	[BindProperty(SupportsGet = true, Name = "pageNum")]
	public int PageNum { get; set; }

	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Query { get; set; }

	// Taxonomy filter (User|Feedback|Project|Reference) — applies to the hybrid search only (the
	// deterministic listing has never filtered by type; adding that is out of this card's scope).
	[BindProperty(SupportsGet = true, Name = "type")]
	public string? Type { get; set; }

	// project (default) | workspace | cascade — which container(s) the search reads, mirroring
	// memory_search's `scope`. Only affects the search path: the plain listing always stays this
	// store, this project (unchanged behavior).
	[BindProperty(SupportsGet = true, Name = "scope")]
	public string? Scope { get; set; }

	// relevance (default) | created | updated — reorders WITHIN the selected set, same as
	// memory_search's `sort`. Ignored (there is no relevance leg) when there's no query.
	[BindProperty(SupportsGet = true, Name = "sort")]
	public string? Sort { get; set; }

	// The scope control is moot when this project IS already the workspace's shared-memory
	// container (MemorySearchScope.ResolveContainersAsync collapses to one leg regardless) — hide
	// it rather than offer a choice with no effect.
	public bool ScopeSelectable => MemorySearchScope.IsScopeSelectable(WorkspaceKey, ProjectKey);

	// The deep-link half of the stable entry URL (…/memory/{store}?key={key}#{key}, MemoryLinks):
	// the SERVER resolves which page holds the key and renders THAT page, so the fragment has a card
	// to land on. A bare fragment cannot: it is never sent to the server, so the entry was silently
	// absent from the DOM for every store bigger than one page.
	[BindProperty(SupportsGet = true, Name = MemoryLinks.KeyParam)]
	public string? Key { get; set; }

	// The key the request asked for AND that this page actually renders — the card is marked
	// `data-highlight="true"` so the highlight does not hang on `:target` alone.
	public string? HighlightKey { get; private set; }

	const int PageSize = 40;

	// True once a non-empty (trimmed) `q` is in play — the page renders Hits instead of Entries.
	public bool IsSearch { get; private set; }

	public IReadOnlyList<MemoryEntry> Entries { get; private set; } = [];
	// SearchEntriesAsync hits (IsSearch only) — each carries its owning Scope/Store, the fused
	// Score and the Retriever ("lexical"/"semantic"), same provenance memory_search returns.
	public IReadOnlyList<MemorySearchScope.Row> Hits { get; private set; } = [];
	// Retriever provenance for the search (null outside IsSearch — no relevance leg ran).
	public SearchRetrievers? Retrievers { get; private set; }
	public int Total { get; private set; }
	// Always false in search mode: SearchEntriesAsync caps by Limit, it does not offset-page: a
	// search result is a bounded top-N selection, not a walkable list (same shape memory_search
	// returns to an agent).
	public bool HasNext { get; private set; }

	// Usage counters per key (spec: memory-usage-observability) — listing mode only, keyed by the
	// bare entry key (single store, single container: unchanged). Viewing this page is curation,
	// not usage — it reads the counters and never increments them.
	public IReadOnlyDictionary<string, MemoryUsageView> Usage { get; private set; } =
		new Dictionary<string, MemoryUsageView>();

	// The search-mode twin of Usage, keyed by MemorySearchScope.UsageKey(scope, store, key) since a
	// cascade search's Hits may span two containers.
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

		if (PageNum < 0) PageNum = 0;

		// A `?key=` deep-link OWNS the page number: the entry's page is computed from its rank in the
		// listing order, so the link keeps working as the store grows (and an explicit ?pageNum is
		// overridden — the key is the more specific ask). Resolution runs against the UNFILTERED
		// listing, so a `?q=` narrowing is dropped for the deep-link; a key that no longer resolves
		// (deleted entry, typo) leaves the page as-is and simply highlights nothing.
		if (!string.IsNullOrWhiteSpace(Key))
		{
			Query = null;
			var found = await _memory.FindActiveEntryPageAsync(ProjectKey, Store, Key, PageSize, ct);
			if (found is { } p)
			{
				PageNum = p;
				HighlightKey = Key;
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
					Sort = ParseSortBy(Sort),
					Limit = PageSize,
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
			var page = await _memory.ListActiveEntriesPageAsync(ProjectKey, Store, Query, PageNum, PageSize, ct);
			Entries = page.Entries;
			HasNext = page.HasNext;
			Total = page.Total;
			// Only load the usage counters for the keys actually rendered on this page.
			var keys = Entries.Select(e => e.Key).ToList();
			Usage = keys.Count == 0
				? new Dictionary<string, MemoryUsageView>()
				: await _memory.GetUsageAsync(ProjectKey, Store, keys, ct);
		}
		Aggregate = await _memory.GetUsageAggregateAsync(ProjectKey, Store, ct: ct);
		return Page();
	}

	// Maps the `sort` query arg onto the service sort axis. null/empty/"relevance" → null (the
	// service's own default: the fused relevance order — sorting BY relevance is only valid
	// implicitly, MemoryService rejects an explicit Sort.By==Relevance combined with no query, and
	// this path always has one). created/updated default to DESC (newest first) — the same
	// "freshest fact first" default the no-query listing documents (MemoryContract.cs).
	static (MemorySortBy By, bool Desc)? ParseSortBy(string? sort) => sort?.Trim().ToLowerInvariant() switch
	{
		"created" => (MemorySortBy.Created, true),
		"updated" => (MemorySortBy.Updated, true),
		_ => null,
	};
}
