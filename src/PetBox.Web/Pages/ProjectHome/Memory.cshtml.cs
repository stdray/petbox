using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Memory.Contract;
using PetBox.Web.Auth;
using PetBox.Web.Memory;
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.ProjectHome;

// Main-UI memory dashboard for a project (/ui/{ws}/{project}/memory). With no `q`, a read-only
// list of named stores from petbox.db metadata (unchanged). A non-empty `q` sweeps the WHOLE
// project's memory (every store, no Filter.Store — the UI twin of `memory_search` with no
// `store` arg: spec search-one-engine-for-human-and-agent) through the same hybrid engine the
// per-store page and MCP both use (IMemoryService.SearchEntriesAsync via MemorySearchScope),
// filterable by type, scoped project/workspace/cascade, sorted relevance/created/updated. This
// is the ONE place a search can span every store at once — the per-store page can only ever
// narrow to the store it is already on. Stores are created by agents via the memory MCP tools.
// WorkspaceViewer: route workspaceKey membership (sysadmin free-pass) — closes
// cross-tenant shared-memory reads that bare [Authorize] allowed.
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class MemoryModel : PageModel
{
	readonly IWorkspaceMemoryDirectory _workspaceMemory;
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

	public MemoryModel(
		IWorkspaceMemoryDirectory workspaceMemory, IProjectDirectory projects, FeatureFlags features, IMemoryService memory,
		IProjectCatalog? catalog = null, IUiState? uiState = null)
	{
		_workspaceMemory = workspaceMemory;
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

	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Query { get; set; }

	[BindProperty(SupportsGet = true, Name = "type")]
	public string? Type { get; set; }

	[BindProperty(SupportsGet = true, Name = "scope")]
	public string? Scope { get; set; }

	[BindProperty(SupportsGet = true, Name = "sort")]
	public string? Sort { get; set; }

	// The query pool's resume token (spec: result-set-pageable) — a KeysetCursor.Encode() string,
	// opaque by contract. Query mode only: this page has no listing branch to page (see class header).
	[BindProperty(SupportsGet = true, Name = "cursor")]
	public string? Cursor { get; set; }

	// ui-search-page-position-and-size: see MemoryStoreModel's identical pair for the full
	// rationale (not part of the cursor fingerprint; lives in the filter form so a size change
	// starts the walk over; `pos` is a plain presentation counter, never mixed into the cursor).
	[BindProperty(SupportsGet = true, Name = "size")]
	public int? Size { get; set; }
	public int EffectiveSize => PageSizeOptions.Resolve(Size);

	[BindProperty(SupportsGet = true, Name = "pos")]
	public int Pos { get; set; }
	int EffectivePos => Pos < 0 ? 0 : Pos;

	// The inclusive 1-based range of rows THIS page shows — never an invented total (the ranked
	// pool's true match count is unknowable mid-walk).
	public int RangeFrom { get; private set; }
	public int RangeTo { get; private set; }

	public bool ScopeSelectable => !WorkspaceMemory.IsWorkspaceContainer(ProjectKey);

	public Project? Project { get; private set; }
	public bool MemoryEnabled => _features.IsEnabled(Feature.Memory);
	public IReadOnlyList<MemoryStoreMeta> Stores { get; private set; } = [];

	public bool IsSearch { get; private set; }
	public IReadOnlyList<MemorySearchScope.Row> Hits { get; private set; } = [];
	public SearchRetrievers? Retrievers { get; private set; }
	public IReadOnlyDictionary<string, MemoryUsageView> HitUsage { get; private set; } =
		new Dictionary<string, MemoryUsageView>();

	// PAGING (spec: result-set-pageable card requirement 1/2) — the same three fields
	// MemoryStoreModel's search branch carries, unified across both memory UI surfaces.
	public bool HasNext { get; private set; }
	public string? NextCursor { get; private set; }
	public string? CursorError { get; private set; }
	public string? Stop { get; private set; }
	public string? PoolBoundaryHint { get; private set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		// Shared-memory routes (/ui/{ws}/$ws-{ws}/memory or /ui/$system/$workspace/memory):
		// lazy-ensure the container so the first UI navigation is not a "Project not found"
		// before any MCP write. No-op when the row already exists (incl. M028 $workspace).
		//
		// Provisioning goes through IWorkspaceMemoryDirectory — the page no longer opens core.db
		// itself; EnsureWorkspaceContainerAsync is idempotent under concurrent GETs (see its doc).
		if (WorkspaceMemory.IsWorkspaceContainer(ProjectKey)
			&& string.Equals(WorkspaceMemory.WorkspaceKeyOfContainer(ProjectKey), WorkspaceKey, StringComparison.Ordinal))
		{
			await _workspaceMemory.EnsureWorkspaceContainerAsync(WorkspaceKey, ct);
		}

		// The route workspace is welded into the lookup — this is the field IDOR
		// (/ui/$system/$ws-other/memory) the page used to reject by filtering after the fact.
		Project = await _projects.GetInWorkspaceAsync(WorkspaceKey, ProjectKey, ct);
		if (Project is null || !MemoryEnabled) return;

		Stores = await _memory.ListStoresAsync(ProjectKey, ct);

		var q = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim();
		IsSearch = q is not null;
		if (!IsSearch) return;

		var sort = ParseSortBy(Sort);
		// EDGE default (spec: search-ranking-mode-is-caller-choice): UI is the speed side.
		// ui-search-ranking-mode-preference: overridable per-user (BrowserState.SearchRankingMode,
		// /ui/me/preferences) — no longer a bare constant. _uiState null (bare unit-test
		// construction, no HttpContext) falls back to the same Speed the constant used to be.
		var rankingMode = _uiState is not null ? (await _uiState.GetAsync(ct)).SearchRankingMode : PetBox.Core.Search.SearchRankingMode.Speed;
		var result = await MemorySearchScope.SearchAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope,
			new SearchRequest<MemoryEntryFilter, MemorySortBy>
			{
				Query = q,
				// No Store filter — sweep every (non-sensitive) store in scope, the project-wide
				// twin of memory_search called with no `store` arg.
				Filter = new MemoryEntryFilter(null, Type),
				Sort = sort,
				Limit = EffectiveSize,
				RankingMode = rankingMode,
				BodyLen = 240, // a snippet — this view lists across stores, not one store's full cards
							   // PAGING (spec: result-set-pageable) — the whole ranked pool, seeked below with a
							   // KeysetCursor exactly like MemoryStoreModel's search branch.
				WholePool = true,
			}, ct);
		Retrievers = result.Retrievers;

		var fingerprint = SearchFingerprint(q, sort, result.DataVersion);
		var afterCursor = result.Rows;
		if (!string.IsNullOrWhiteSpace(Cursor))
		{
			try
			{
				var decoded = KeysetCursor.Decode(Cursor, fingerprint, "memory-search");
				// THE POOL COMMITMENT, checked first — the walk is bound to the pool its order came out
				// of, because a reranked order is a property of ONE PASS (measured). Reached here only
				// when the reader asked for Precision: the UI's edge default is Speed, whose RRF order a
				// rebuild reproduces exactly and which therefore keeps paging across a cold pool.
				KeysetCursor.AssertPoolAlive(result.PoolRebuiltByRerank, "memory-search");
				if (!string.IsNullOrEmpty(result.PoolOrderHash))
					decoded.AssertPoolOrder(result.PoolOrderHash, "memory-search");
				afterCursor = KeysetCursor.Advance(
					result.Rows, decoded, r => ("", r.Store + "\x1f" + r.Entry.Key, r.Scope),
					SearchCursorComparison, desc: false, "memory-search");
			}
			catch (ArgumentException ex)
			{
				CursorError = ex.Message;
				afterCursor = result.Rows;
			}
		}

		Hits = afterCursor.Take(EffectiveSize).ToList();
		HasNext = afterCursor.Count > EffectiveSize;
		if (HasNext)
		{
			var last = Hits[^1];
			NextCursor = new KeysetCursor(fingerprint, "", last.Store + "\x1f" + last.Entry.Key, last.Scope,
				result.PoolOrderHash ?? "").Encode();
		}
		// WHY THE WALK STOPPED — stated, not implied (card requirement 2).
		Stop = HasNext ? "more" : result.PoolBounded ? "pool-boundary" : "exhausted";
		// ui-search-page-position-and-size: the range of rows THIS page shows.
		if (Hits.Count > 0) { RangeFrom = EffectivePos + 1; RangeTo = EffectivePos + Hits.Count; }
		PoolBoundaryHint = Stop == "pool-boundary" ? PoolBoundaryHintText : null;
		HitUsage = await MemorySearchScope.LoadUsageAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, Scope, Hits, ct);
	}

	static (MemorySortBy By, bool Desc)? ParseSortBy(string? sort) => sort?.Trim().ToLowerInvariant() switch
	{
		"created" => (MemorySortBy.Created, true),
		"updated" => (MemorySortBy.Updated, true),
		_ => null,
	};

	// Everything that decides the QUERY's selection + order, hashed into the cursor — the project-wide
	// twin of MemoryStoreModel.SearchFingerprint (no Store axis here: this page sweeps every store).
	string SearchFingerprint(string? q, (MemorySortBy By, bool Desc)? sort, string? dataVersion) =>
		KeysetCursor.FingerprintOf(
			"memory-search", WorkspaceKey, ProjectKey, NormalizeScope(Scope), NormalizeType(Type), q,
			sort?.By.ToString(), sort?.Desc.ToString(), dataVersion);

	static string NormalizeScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
	{
		"workspace" => "workspace",
		"cascade" => "cascade",
		_ => "project",
	};

	static string NormalizeType(string? type) => string.IsNullOrWhiteSpace(type) ? "" : type.Trim().ToLowerInvariant();

	// Same reasoning as MemoryStoreModel.SearchCursorComparison: the relevance order carries no sound
	// scalar, resumption is by identity only, and reaching this delegate means the pinning above should
	// already have refused the token — refuse explicitly rather than guess a boundary.
	static Comparison<string> SearchCursorComparison => (_, _) => throw new ArgumentException(
		"memory-search: the row this cursor names is no longer in the ranked pool, and a relevance position "
		+ "cannot be re-derived from it (the order is score-fused and, in cascade scope, freshness-blended). "
		+ "Drop the cursor and start the search over.");

	const string PoolBoundaryHintText =
		"Ranking depth reached: more entries matched this search than relevance ranking looked at, so this "
		+ "is a prefix of the match set, not all of it — and there is no further page to fetch, because the "
		+ "rest was never ranked. Narrow the search (type, scope, a more specific query) to reach it.";
}
