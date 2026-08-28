using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Hybrid;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Rendering;
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one task board (/ui/{ws}/{project}/tasks/{board}). Shows
// the currently-active task nodes (ActiveTo == null) in plan-tree order. Reads and
// the quick-add write both go through ITasksService — the page never opens the DB
// context itself, so quick-add gets the same NodeId/status handling the MCP path does.
// viewer-member-consistency: the class policy is WorkspaceViewer — a Viewer must be able to READ
// the board — but every OnPost* handler here is a MUTATION (comment add/edit/delete, quick-add)
// and guards itself to Member+ (see each handler below).
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class TaskBoardModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;
	readonly ICommentService _comments;
	readonly ISettingsResolver _settings;
	// Optional ctor param (the SearchOrderingPolicies pattern): DI always supplies it — IMemoryService
	// is registered unconditionally — and a page-model unit test that doesn't exercise memory
	// autolinking may omit it. Null = no memory refs, keys render literal.
	readonly PetBox.Memory.Contract.IMemoryService? _memory;

	// board-search-stem-lookup: caches OnGetSearchIndexAsync's built BoardSearchIndex keyed by
	// (project,board,boardVersion).
	//
	// ON DISK now, not in RAM (work/cache-backend-decision). The reason is size against value: the
	// index is ~124 KB gzip for one 481-node board, it is worth keeping for as long as the board does
	// not change, and holding every active board's copy in the process is exactly the unbounded
	// memory this move exists to shed. The key is already ETag-versioned, so nothing here invalidates
	// anything — a superseded index is simply never asked for again and ages out on TTL.
	//
	// Same optional-DI-param posture as _memory above: DI always supplies it, and a bare
	// `new TaskBoardModel(...)` unit test that never calls this handler passes none. NULL therefore
	// means "no cache", not "broken" — the handler below rebuilds the index every call, which is what
	// a unit test wants anyway. That is a change from the old throwaway `new MemoryCache(...)`, which
	// pretended to be a cache nobody could ever read from.
	readonly HybridCache? _searchIndexCache;

	// Optional ctor param, same posture as _memory above: needed only to gate MemoryRefMap's derived
	// workspace-container leg (SandboxContainment.PermitsAsync) — DI always supplies it (IProjectCatalog
	// is registered unconditionally), a bare unit-test construction that never exercises memory
	// autolinking may omit it.
	readonly IProjectCatalog? _catalog;

	public TaskBoardModel(FeatureFlags features, ITasksService tasks, ICommentService comments,
		ISettingsResolver settings, PetBox.Memory.Contract.IMemoryService? memory = null,
		HybridCache? searchIndexCache = null, IProjectCatalog? catalog = null)
	{
		_features = features;
		_tasks = tasks;
		_comments = comments;
		_settings = settings;
		_memory = memory;
		_searchIndexCache = searchIndexCache;
		_catalog = catalog;
	}

	// Memory-key autolinking is a Memory-module affordance: with the feature off there is no store
	// page to link to, so the map stays empty and every key renders as literal text. (The flag gates
	// pages, not DI — IMemoryService itself is registered unconditionally.)
	Task<IReadOnlyDictionary<string, NodeRefTarget>> BuildMemoryRefsAsync(IEnumerable<string?> bodies, CancellationToken ct) =>
		_memory is not null && _features.IsEnabled(Feature.Memory)
			? MemoryRefMap.BuildAsync(_memory, User, _catalog, WorkspaceKey, ProjectKey, bodies, ct)
			: Task.FromResult<IReadOnlyDictionary<string, NodeRefTarget>>(
				new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal));

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "board")]
	public string Board { get; set; } = string.Empty;

	// Nodes in plan-tree (DFS) render order; ClosedWithActiveDescendant holds the
	// NodeIds of Done/Cancelled nodes that must stay visible under "active only"
	// because a descendant is still open (else the children would orphan).
	public IReadOnlyList<TaskNodeView> Nodes { get; private set; } = [];
	public IReadOnlySet<string> ClosedWithActiveDescendant { get; private set; }
		= new HashSet<string>(StringComparer.Ordinal);

	// board-page-cost: the board card NEVER renders a comment thread (that stayed a node-detail-
	// page affordance — comments-ui's "visible to humans" promise is satisfied there). Each card
	// shows only a "💬 N" count chip linking to the node page, sourced from ONE aggregate query
	// (ICommentService.CountForBoardAsync) instead of loading and DFS-flattening every comment
	// body/thread on the board (the old CommentThreads dictionary this replaces).
	public IReadOnlyDictionary<string, int> CommentCounts { get; private set; }
		= new Dictionary<string, int>(StringComparer.Ordinal);

	// Explicit view-mode request from the URL (board-view-modes/board-view-persistence). Null
	// = "not specified" — distinct from an explicit `?view=tree`, so the resolver
	// (BoardViewModeRegistry.Resolve) can fall through to the methodology's defaultView
	// before landing on the builtin Tree default. `?view=tags&by=area,concern` selects an
	// ordered list of tag namespaces; that projection is a pure view over the same nodes and
	// never touches part_of (tag-grouping-is-projection).
	[BindProperty(SupportsGet = true, Name = "view")]
	public string? ViewMode { get; set; }

	[BindProperty(SupportsGet = true, Name = "by")]
	public string? By { get; set; }

	// board-view-fields: which node properties this board's cards/rows/columns show — a
	// PARAMETER of every view mode, not a per-mode ceiling (spec board-view-fields). Same
	// resolution shape as ViewMode just above: an explicit `?fields=a&fields=b` (repeatable query
	// key — ASP.NET binds it to the array) plus the `fieldsSet` marker that disambiguates "the
	// dialog submitted zero checked boxes" from "no `fields` in the URL at all" (an empty
	// FieldsParam alone is indistinguishable from absent — unchecked checkboxes don't post).
	[BindProperty(SupportsGet = true, Name = "fields")]
	public string[]? FieldsParam { get; set; }

	[BindProperty(SupportsGet = true, Name = "fieldsSet")]
	public string? FieldsSetParam { get; set; }

	// The mode+kind default (board-view-fields dialog pre-check state) — always computed, whether
	// or not the request carried an explicit selection.
	public BoardFieldConfig DefaultFields { get; private set; } = BoardFieldConfig.None;

	// What THIS render actually shows: FieldsParam when the request explicitly declared a
	// selection (FieldsSetParam present — including a deliberately empty one), else the saved
	// per-board DB preference (board-view-cross-device), else DefaultFields. Read by _TaskNodeCard
	// (via TaskNodeCard.Fields below) and directly by the view partials that don't route through a
	// TaskNodeCard (Kanban/Outline/Table).
	public BoardFieldConfig Fields { get; private set; } = BoardFieldConfig.None;

	// kanban-column-picker: which kanban columns (workflow-status slugs) are visible — the SAME
	// URL-contract shape as `fields`/`fieldsSet` above (a repeated `columns=` query key plus a
	// `columnsSet` disambiguator), but the vocabulary is THIS board's own KanbanColumns, not a
	// fixed BoardFieldNames-style list.
	[BindProperty(SupportsGet = true, Name = "columns")]
	public string[]? ColumnsParam { get; set; }

	[BindProperty(SupportsGet = true, Name = "columnsSet")]
	public string? ColumnsSetParam { get; set; }

	// What THIS render actually shows: ColumnsParam when the request explicitly declared a
	// selection (ColumnsSetParam present — including a deliberately empty one), else the saved
	// per-board DB preference, else "every column visible" (kanban-column-picker bullet 4 — the
	// unchanged-until-chosen default). Read by _BoardViewKanban to decide which KanbanColumns to
	// render; NEVER consulted by active-only filtering/sorting/other view modes (presentation
	// only, kanban-column-picker bullet 6).
	public BoardColumnConfig VisibleColumns { get; private set; } = BoardColumnConfig.None;

	// decision-pending-has-no-ui: "only nodes waiting on the owner's decision" — the SAME
	// URL-contract shape as `view`/`by` above (board-view-cross-device): an explicit `?decisionPending=`
	// both renders AND writes the per-board saved preference; bool? (not string[]) already tells
	// "absent from the URL" (null) apart from "explicitly asked, either way" (true/false), so unlike
	// Fields/Columns this needs no extra `...Set` disambiguator.
	[BindProperty(SupportsGet = true, Name = "decisionPending")]
	public bool? DecisionPendingParam { get; set; }

	// What THIS render actually filtered on: DecisionPendingParam when the request explicitly named
	// it, else the saved per-board preference, else false (show everything — a board nobody has
	// ever asked to narrow must render exactly as it always has). Read by the view to draw the
	// toggle's current state.
	public bool DecisionPendingOnly { get; private set; }

	// board-filters-server-state: active-only / sort — GLOBAL (board-independent) [Setting]
	// preferences resolved from BrowserState. Rendered into _BoardFilterSort's controls (checked/
	// selected/arrow) AND used to compute each row's Hidden flag / DFS sort order server-side, so
	// the first response already matches what ts/board.ts's initBoardPage reads off the DOM on
	// load — no post-paint filter/reorder.
	public bool ActiveOnly { get; private set; } = true;
	public string SortBy { get; private set; } = BoardSortKeys.Priority;
	public bool SortDesc { get; private set; }

	// board-filters-server-state: which nodes are collapsed on THIS board (cookie branch, per
	// (project,board) — see BrowserState.CollapsedByBoard). Read by _TaskNodeCard (caret glyph +
	// data-collapsed) and IsHiddenByCollapse below (descendant hiding) — tree/outline only, exactly
	// like the client mechanism it replaces (kanban/table cards never carried a data-parent-id for
	// the old client-side hiddenByCollapse to walk, so they're unaffected here too).
	public IReadOnlySet<string> CollapsedNodeIds { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

	// NodeId -> its own ParentNodeId, built once from Nodes — IsHiddenByCollapse walks this
	// upward from a node's parent to find a collapsed ancestor anywhere in the chain, not just the
	// immediate parent.
	Dictionary<string, string?> _parentOf = new(StringComparer.Ordinal);

	// Whether `parentNodeId`'s ancestor chain contains a collapsed node — mirrors ts/board.ts's
	// hiddenByCollapse exactly (same guard against a part_of cycle).
	public bool IsHiddenByCollapse(string? parentNodeId)
	{
		var cur = parentNodeId;
		var guard = 0;
		while (cur is not null && guard++ < 1000)
		{
			if (CollapsedNodeIds.Contains(cur)) return true;
			_parentOf.TryGetValue(cur, out cur);
		}
		return false;
	}

	// board-filters-server-state / board-sort-impl: the server-side twin of ts/board.ts's
	// sortKeyValue+compareSortValues — an unrecognized sortBy (a stale saved value referencing a
	// removed key) falls back to "priority", never throws. `desc` flips the PRIMARY comparison
	// only; the secondary Key tie-break stays ascending regardless of `desc` — this matches the
	// EXACT previous hardcoded behavior when sortBy is the default ("priority", desc:false), which
	// is what every pre-existing ordering test still asserts.
	public static IComparer<TaskNodeView> SortComparer(string sortBy, bool desc)
	{
		Comparison<TaskNodeView> primary = sortBy switch
		{
			BoardSortKeys.Created => (a, b) => (a.CreatedAt?.Ticks ?? 0).CompareTo(b.CreatedAt?.Ticks ?? 0),
			BoardSortKeys.Updated => (a, b) => (a.UpdatedAt?.Ticks ?? 0).CompareTo(b.UpdatedAt?.Ticks ?? 0),
			BoardSortKeys.Title => (a, b) => string.Compare(TitleKey(a), TitleKey(b), StringComparison.Ordinal),
			_ => (a, b) => a.Priority.CompareTo(b.Priority),
		};
		return Comparer<TaskNodeView>.Create((a, b) => desc ? -primary(a, b) : primary(a, b));
	}

	static string TitleKey(TaskNodeView n) => (string.IsNullOrEmpty(n.Title) ? n.Key : n.Title).ToLowerInvariant();

	// The mode BoardViewModeRegistry.Resolve settled on (explicit -> methodology defaultView
	// -> Tree) — RENDERABLE by construction (Resolve never returns a mode without a partial).
	// Exposed so the switcher can mark the active button and the client-side persistence
	// script (board-view-meta) knows what the server actually rendered.
	public string ResolvedViewMode { get; private set; } = PetBox.Tasks.Workflow.BoardViewModeNames.Tree;

	// The partial TaskBoard.cshtml dispatches the content pane to — registry-driven, never a
	// hardcoded name in the .cshtml. Falls back to the tree partial whenever the resolved
	// mode's own content isn't actually usable (e.g. ResolvedViewMode is "tags" but `by` was
	// invalid/empty — the existing tag-grouping fallback, preserved verbatim).
	public string ContentPartialName { get; private set; } = "_BoardViewTree";

	public bool IsTagView { get; private set; }
	public IReadOnlyList<string> GroupDims { get; private set; } = []; // ordered namespaces actually applied
	public IReadOnlyList<GroupRow> GroupRows { get; private set; } = []; // flattened tag-groups pane

	// Kanban view mode (board-view-mode-framework): one column per FSM status, sourced from
	// WorkflowBlocks — NEVER hardcoded, so a board on a different methodology still gets its own
	// stage columns. A multi-block kind (two type FSMs on one board) unions the blocks' statuses,
	// first-seen order, so a shared status name collapses into one column instead of duplicating.
	public sealed record KanbanColumn(string Slug, string Name);
	public IReadOnlyList<KanbanColumn> KanbanColumns { get; private set; } = [];

	// Outline view mode (board-view-mode-framework): which OutlineRevealModeNames constant the
	// _BoardViewOutline partial renders with. Resolved via Runtime.OutlineReveal(KindSlug) — DATA
	// on the kind (MethodologyKindDef.OutlineReveal), not a process-role enum lookup, so a `spec`
	// board provisioned from the quartet/classic BUILTIN TEMPLATE (its kinds materialize into a
	// stored MethodologyDefinition — methodology-template-storage) still resolves inline-lazy.
	// spec's bodies are one short normative line (inline-lazy: cheap to fetch and read in place);
	// every other kind (incl. a project-defined custom kind, where body length is unknown) gets
	// `navigate`, the conservative default.
	public string OutlineRevealMode { get; private set; } = OutlineRevealModeNames.Navigate;

	public bool ShowQuickAdd { get; private set; }

	// Set when a comment mutation (add/reply/edit/delete) was rejected — a guard violation or
	// an optimistic-concurrency conflict re-renders the board with the message inline rather
	// than silently dropping it (mirrors TaskBoardNodeModel.Error / edit-respects-guards).
	public string? Error { get; private set; }

	// The project's commit-view URL template (RepoSettings, Scope.Project). When set, the commit-ref
	// chip on each card links to it and commit hashes in node/comment bodies autolink. Empty = off.
	public string? CommitUrlTemplate { get; private set; }

	// Resolved `[[slug]]` mentions across all card bodies + comment bodies (node-ref-autolink),
	// keyed by the mentioned slug. Threaded into each card so the renderer links resolvable
	// mentions. Empty when nothing resolved.
	public IReadOnlyDictionary<string, NodeRefTarget> NodeRefs { get; private set; }
		= new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);

	// Resolved memory-entry keys across all card bodies + comment bodies (memory-key-mention-link),
	// keyed by the mentioned key. Two queries for the whole page (project + workspace container);
	// unresolved/ambiguous keys are absent and stay literal. Empty when Memory is off.
	public IReadOnlyDictionary<string, NodeRefTarget> MemoryRefs { get; private set; }
		= new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);

	// live-verification finding: every rendered node's Observation.FixedByNodeId resolved to a
	// slug in ONE batch (ObservationFixedByResolver.ResolveManyAsync), threaded into each
	// TaskNodeCard/TaskTableModel exactly like NodeRefs/MemoryRefs above. Empty on every
	// non-observation board.
	public IReadOnlyDictionary<string, LinkDto> ObservationFixedByLinks { get; private set; }
		= new Dictionary<string, LinkDto>(StringComparer.Ordinal);

	// The board's EFFECTIVE process, resolved through MethodologyRuntime — the same seam
	// the MCP tools / TasksService use (project definition first, preset fallback), so a
	// definition-declared custom kind renders its own statuses/terminality instead of
	// falling back to the Simple preset. KindSlug is the stored board kind (the runtime
	// lookup key); KindName is what the badge shows (a defined kind verbatim, else the
	// parsed preset name — `free`/unknown read as `simple`, exactly as before).
	public MethodologyRuntime Runtime { get; private set; } = MethodologyRuntime.PresetsOnly;
	public string? KindSlug { get; private set; }
	public string KindName { get; private set; } = string.Empty;

	// spec methodology-inactive-visibility: true when this board belongs (membership) to an
	// OPEN methodology instance that is not the project's current effective default — a
	// COMPUTED view (this comparison), never a stored flag. A board with no instance membership
	// (legacy/simple) is never "inactive" by this measure.
	public bool InstanceInactive { get; private set; }

	// closed-board-disabled-display: null = open. Mirrors the list/sidebar closed badge
	// (TaskBoardMeta.ClosedAt) onto this content page — the write path already rejects
	// (TasksService.UpsertAsync) a closed board, so this drives the badge + hides quick-add.
	public DateTime? ClosedAt { get; private set; }

	// The board's workflow surface (per-type FSM blocks) + its JSON island for the "View
	// workflow" modal. WorkflowBlocks drives the header triggers (one per block); WorkflowJson
	// is the payload ts/workflow-viz.ts renders. Resolved through MethodologyRuntime, so
	// user-defined methodologies visualize out of the box.
	public IReadOnlyList<WorkflowBlock> WorkflowBlocks { get; private set; } = [];
	public string? WorkflowJson { get; private set; }

	// One flattened row of the tag-groups pane: a group HEADER (Node null) at nesting `Depth`,
	// or a node CARD (Node set) sitting just under its deepest group. Flattening keeps the
	// Razor a single loop — the same shape the part_of pane already renders.
	public sealed record GroupRow(int Depth, string? GroupKey, string? Delivery, TaskNodeView? Node);

	// Everything the shared _TaskNodeCard partial needs to render one node card in either
	// pane. `Runtime` + `KindSlug` let the card classify statuses per the board's EFFECTIVE
	// kind (definition first, preset fallback). `Depth` drives the indent (part_of depth in
	// the tree pane, 0 in the tag-groups pane — grouping is the structure there).
	// `HasChildren` shows the collapse caret (tree only). The tree-interactivity data-*
	// (parent/closed/keep-visible) are inert in the tag pane because ts/board.ts binds only
	// to the tree's board-nodes list.
	public sealed record TaskNodeCard(
		string WorkspaceKey, string ProjectKey, string Board, MethodologyRuntime Runtime,
		string? KindSlug, TaskNodeView Node,
		int Depth, bool Closed, bool KeepVisible, bool HasChildren,
		// board-page-cost: a COUNT only (0 = no chip) — the card never renders the thread itself
		// (see CommentCounts' own header comment).
		int CommentCount, BoardFieldConfig Fields, string? CommitUrlTemplate = null,
		IReadOnlyDictionary<string, NodeRefTarget>? NodeRefs = null,
		IReadOnlyDictionary<string, NodeRefTarget>? MemoryRefs = null,
		// board-filters-server-state: server-computed hidden (active-only OR a collapsed ancestor)
		// and self-collapsed (this node's OWN caret state) — an inline `display:none` + the caret
		// glyph/`data-collapsed` attribute render correctly on the FIRST response instead of
		// ts/board.ts hiding/flipping them after paint. Default false: the tag-groups projection
		// (_BoardViewTags.cshtml) doesn't compute or thread these through — that pane is a Hidden
		// (from-the-switcher) projection over the tree, out of scope for this pass, so its cards
		// simply render as always-visible/never-collapsed, same as before this change.
		bool Hidden = false, bool CollapsedSelf = false,
		// live-verification finding: the regression banner's fixed-by target, resolved to a slug
		// once per PAGE (TaskBoardModel.LoadAsync, ObservationFixedByResolver.ResolveManyAsync) and
		// threaded through like NodeRefs/MemoryRefs above — never re-resolved per card. Empty map on
		// every non-observation board (nothing to resolve).
		IReadOnlyDictionary<string, LinkDto>? ObservationFixedByLinks = null);

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();
		return await LoadAsync(ct);
	}

	// Outline view mode, inline-lazy reveal (board-view-mode-framework / board-body-truncate):
	// fired by the <details> `toggle` htmx trigger in _BoardViewOutline.cshtml — the node's body
	// is fetched ONLY when its heading is expanded, never bundled into the initial board render.
	// Returns the same _MdBody fragment a board card renders, so autolinking (commit refs,
	// [[slug]] mentions) matches every other body surface. 404s a node id from a different board
	// (or one that no longer exists) — the handler never trusts nodeId alone.
	public async Task<IActionResult> OnGetNodeBodyAsync(string nodeId, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		var detail = await _tasks.GetNodeAsync(ProjectKey, nodeId, ct);
		if (detail is null || !string.Equals(detail.Board, Board, StringComparison.Ordinal)) return NotFound();

		var commitUrlTemplate = (await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey, ct)).CommitUrlTemplate;
		var nodeRefs = await NodeRefMap.BuildAsync(_tasks, WorkspaceKey, ProjectKey, [detail.Node.Body], ct);
		var memoryRefs = await BuildMemoryRefsAsync([detail.Node.Body], ct);

		return Partial("_MdBody", new MdBodyModel
		{
			Body = detail.Node.Body,
			TestId = "outline-body-content",
			CommitUrlTemplate = commitUrlTemplate,
			NodeRefs = nodeRefs,
			MemoryRefs = memoryRefs,
		});
	}

	// board-search-stem-lookup: the client-fetched {stem -> node index} search lookup ts/board.ts
	// uses for instant text filtering — replaces the old per-card `data-search` blob that stamped
	// title+key+FULL BODY+tags onto every card (the body shipped TWICE: rendered HTML + the
	// attribute; 294KB gzip on the real `work` board). Same door as OnGetNodeBodyAsync above: a
	// page handler on THIS model reuses the class-level [Authorize(Policy="WorkspaceViewer")] +
	// the BoardExistsAsync guard — no new auth surface.
	//
	// Owner's explicit correction to the first draft of this card: the lookup must NOT ride in the
	// page's own HTML (stemming ~500 nodes' worth of text on every board render would undo the
	// 380ms->20-60ms win board-page-cost just bought). So this is its OWN cacheable resource:
	//   - ETag = ITasksService.GetBoardChangeStampAsync — a SCALAR SQL probe (MAX(Version) over
	//     plan_nodes UNIONED with the latest node_tag mutation timestamp; see that method's own
	//     comment for why tags need their own leg), never a node materialization — cheap enough to
	//     run on EVERY call to this handler, including a 304 revalidation that changed nothing.
	//     (The first draft called GetAsync(includeBody:false) here, which is body-less but still
	//     materializes every node row — review finding: wrong cost for "did anything change".)
	//   - Cache-Control "private, no-cache": a browser caches the body but MUST revalidate with
	//     If-None-Match before using it — an unchanged board round-trips a 304 (small) instead of
	//     the ~124KB gzip body, but a changed one is never served stale.
	//   - A server-side IMemoryCache holds the BUILT index keyed by (project,board,stamp), so a
	//     given board state is stemmed ONCE regardless of how many browsers/tabs ask for it — the
	//     bodies needed to build it are read via a SEPARATE, independent GetAsync(includeBody:true)
	//     call, only on a cache miss; the page's own render is untouched (still skips Body whenever
	//     its view mode doesn't show it).
	public async Task<IActionResult> OnGetSearchIndexAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		var stamp = await _tasks.GetBoardChangeStampAsync(ProjectKey, Board, ct);
		var etag = $"\"sidx-{stamp.NodeVersion}-{stamp.TagStamp?.Ticks ?? 0}\"";
		string? ifNoneMatch = null;
		if (HttpContext is not null)
		{
			ifNoneMatch = HttpContext.Request.Headers.IfNoneMatch.ToString();
			HttpContext.Response.Headers.CacheControl = "private, no-cache";
			HttpContext.Response.Headers.ETag = etag;
		}
		if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
			return StatusCode(304);

		// v1: the payload SHAPE version, the same role KeysetCursor.FormatVersion plays for a cursor
		// token. It rides in the key rather than inside the JSON because the key is already the only
		// thing that decides identity here — an index written by a build with a different
		// BoardSearchIndex shape is then never read at all, and ages out on its own TTL instead of
		// being fetched and rejected on every request. A foreign version is a MISS, never garbage and
		// never an error.
		var cacheKey = $"board-search-index:v1:{ProjectKey}:{Board}:{etag}";
		var index = _searchIndexCache is null
			? await BuildSearchIndexAsync(ct)
			: await _searchIndexCache.GetOrCreateAsync(cacheKey, BuildSearchIndexAsync, SearchIndexCacheOptions,
				tags: null, cancellationToken: ct);
		return new JsonResult(index);
	}

	// The bodies needed to build the index are read via a SEPARATE, independent
	// GetAsync(includeBody:true) call, only on a cache MISS; the page's own render is untouched (it
	// still skips Body whenever its view mode doesn't show it).
	async ValueTask<BoardSearchIndex> BuildSearchIndexAsync(CancellationToken ct)
	{
		var full = await _tasks.GetAsync(ProjectKey, Board, includeClosed: true, includeBody: true, ct: ct);
		return BoardSearchIndexBuilder.Build(full.Nodes);
	}

	// 30 minutes, as before. L1 OFF in both directions: a 124 KB index per active board is precisely
	// the kind of thing that should not accumulate in the process — and the browser already holds its
	// own copy behind the ETag, so the memory tier would be a third copy of the same bytes.
	static readonly HybridCacheEntryOptions SearchIndexCacheOptions = new()
	{
		Expiration = TimeSpan.FromMinutes(30),
		Flags = HybridCacheEntryFlags.DisableLocalCacheRead | HybridCacheEntryFlags.DisableLocalCacheWrite,
	};

	// comments-ui-edit: add a comment (or, when parentId is set, a reply) under `nodeId` — a
	// hidden form field, since this page renders MANY node cards (unlike the node detail page,
	// which resolves its one node from the bound route). Goes through ICommentService.AddAsync,
	// the low-ceremony UI door (the comments_upsert MCP verb shares the same guards).
	public async Task<IActionResult> OnPostCommentAddAsync(string nodeId, string? parentId, string body, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		try
		{
			var author = User.Identity?.Name ?? "system";
			var result = await _comments.AddAsync(ProjectKey, Board, nodeId, parentId, author, body, tags: null, ct);
			if (!result.Applied)
				return await LoadAsync(ct, "Could not add the comment — refresh and try again.");
		}
		catch (ArgumentException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}

	// comments-ui-edit: edit a comment's body in place. `version` is the watermark baseline the
	// form rendered with; a stale one (Applied:false) is surfaced as Error, not clobbered.
	public async Task<IActionResult> OnPostCommentEditAsync(string id, string body, long version, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		try
		{
			var result = await _comments.EditAsync(ProjectKey, Board, id, body, tags: null, version, ct);
			if (!result.Applied)
				return await LoadAsync(ct, "This comment changed since the page was opened — refresh and redo your edit.");
		}
		catch (ArgumentException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}

	// comments-ui-edit: soft-delete a comment. Rejected (InvalidOperationException) while it
	// still has active replies — surfaced inline instead of a raw 500.
	public async Task<IActionResult> OnPostCommentDeleteAsync(string id, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		try
		{
			await _comments.DeleteAsync(ProjectKey, Board, id, ct);
		}
		catch (InvalidOperationException ex)
		{
			return await LoadAsync(ct, ex.Message);
		}
		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}

	// Shared load for the GET and the comment-mutation error re-render: everything OnGetAsync
	// used to do inline, now reusable so a rejected comment mutation can re-render the SAME
	// board with Error set, instead of a bare redirect that drops the message.
	async Task<IActionResult> LoadAsync(CancellationToken ct, string? error = null)
	{
		Error = error;
		string? instanceName;
		(Runtime, KindSlug, ClosedAt, instanceName) = await ResolveProcessAsync(ct);
		KindName = Runtime.KindName(KindSlug);
		InstanceInactive = !string.IsNullOrEmpty(instanceName)
			&& !string.Equals(instanceName, await _tasks.ResolveDefaultMethodologyInstanceAsync(ProjectKey, ct), StringComparison.OrdinalIgnoreCase);

		// board-view-cross-device / board-filters-server-state: resolve BoardPreferences (DB,
		// Scope.User) and BrowserState.CollapsedByBoard (cookie) BEFORE deciding view/fields/
		// active-only/sort/collapse, the same "resolve before render" discipline every other
		// UI-state consumer follows (ui-state-framework). Deliberately NOT IUiState/BrowserState's
		// DB branch here: (a) IUiState throws outside a real HTTP request (by design — see
		// UiState.cs), and TaskBoardModel is unit-tested via a bare `new TaskBoardModel(...)` with
		// no HttpContext at all (TaskBoardViewModeTests); (b) board preferences live on their OWN
		// record (BoardPreferences.cs), not BrowserState — see that file for why. Guarding
		// HttpContext/User ourselves (falling back to record defaults when absent) keeps those
		// bare-constructed tests green while a real request still gets the full resolve.
		var userIdString = HttpContext is not null ? User.FindFirst(PetBoxClaims.UserId)?.Value : null;
		var boardPrefs = userIdString is not null
			? await _settings.GetAsync<BoardPreferences>(Scope.User, userIdString, ct)
			: new BoardPreferences();
		var cookieValue = HttpContext is not null && HttpContext.Request.Cookies.TryGetValue(UiStateResolver.CookieName, out var cv)
			? cv
			: null;
		var browserState = UiStateResolver.ApplyBrowserState(new BrowserState(), cookieValue);

		var boardPrefKey = $"{ProjectKey}/{Board}";
		var savedPref = boardPrefs.ViewPreferences.GetValueOrDefault(boardPrefKey);

		ActiveOnly = boardPrefs.ActiveOnly;
		SortBy = BoardSortKeys.IsKnown(boardPrefs.SortBy) ? boardPrefs.SortBy : BoardSortKeys.Priority;
		SortDesc = boardPrefs.SortDesc;
		CollapsedNodeIds = browserState.CollapsedByBoard.TryGetValue(boardPrefKey, out var collapsedArr)
			? new HashSet<string>(collapsedArr, StringComparer.Ordinal)
			: new HashSet<string>(StringComparer.Ordinal);

		// board-view-cross-device resolution order: explicit `view` query-param (a shareable
		// override, and the thing that WRITES the preference below) -> the saved per-(project,board)
		// DB preference -> this board's kind's methodology defaultView -> the builtin Tree default.
		// No client redirect anywhere in this chain (board-view-cross-device: the OLD
		// window.location.replace() when a localStorage pick disagreed with the server's own
		// resolution is gone — the server now resolves the SAME preference before it ever renders).
		// Always lands on a RENDERABLE mode (BoardViewModeRegistry.Resolve never returns an entry
		// without a shipped partial).
		var effectiveViewRequest = ViewMode ?? savedPref?.Mode;
		ResolvedViewMode = BoardViewModeRegistry.Resolve(effectiveViewRequest, Runtime.DefaultView(KindSlug));
		var effectiveBy = By ?? savedPref?.By;
		// decision-pending-has-no-ui: same cascade as ViewMode/By just above.
		DecisionPendingOnly = DecisionPendingParam ?? savedPref?.DecisionPendingOnly ?? false;
		// closed-board-disabled-display: a closed board never shows quick-add, regardless of
		// what the kind would otherwise allow — mirrors the server-side reject in UpsertAsync.
		ShowQuickAdd = Runtime.QuickAddAllowed(KindSlug) && ClosedAt is null;

		// Project-scoped commit-view template (cascades to workspace/system); empty when unset.
		CommitUrlTemplate = (await _settings.GetAsync<RepoSettings>(Scope.Project, ProjectKey, ct)).CommitUrlTemplate;

		// The board's FSM surface, embedded for the "View workflow" modal (a few KB — no extra endpoint).
		var workflow = await _tasks.GetBoardWorkflowAsync(ProjectKey, Board, ct);
		WorkflowBlocks = workflow.Workflows;
		WorkflowJson = WorkflowGraphJson.Serialize(workflow);

		// Kanban's columns are just this same WorkflowBlocks surface, reshaped — always computed
		// (cheap; WorkflowBlocks is already in hand) rather than gated on ResolvedViewMode, same
		// posture as WorkflowBlocks/WorkflowJson above.
		KanbanColumns = WorkflowBlocks
			.SelectMany(b => b.Workflow.Statuses)
			.GroupBy(s => s.Slug, StringComparer.OrdinalIgnoreCase)
			.Select(g => new KanbanColumn(g.Key, g.First().Name))
			.ToList();
		var knownColumnSlugs = KanbanColumns.Select(c => c.Slug).ToList();

		// Outline's reveal mode is DATA on the kind (Runtime.OutlineReveal), not a PresetKind
		// lookup — PresetKind nulls out for any DEFINED kind (its correct guard for process-role
		// behavior), but a `spec` board provisioned from the quartet/classic builtin template
		// stores its kinds as a materialized definition too, which made the inline-lazy branch
		// unreachable for every real board. A definition-declared custom kind's body length is
		// still unknown by default (OutlineReveal null → the conservative `navigate` fallback)
		// unless the definition opts in.
		OutlineRevealMode = Runtime.OutlineReveal(KindSlug);

		// board-view-fields / board-view-cross-device: explicit `?fields=`+`fieldsSet=1` wins (and
		// is what WRITES the preference below) -> the saved per-board DB preference -> the
		// mode+kind default. FieldsSetParam is the disambiguator between "no `fields` in the URL"
		// (use the saved/default) and "the dialog submitted a deliberately empty selection"
		// (FieldsParam null or []), which an absent-vs-empty check on FieldsParam alone can't tell
		// apart (unchecked checkboxes don't post).
		DefaultFields = BoardFieldConfig.Default(ResolvedViewMode, Runtime, KindSlug, OutlineRevealMode == OutlineRevealModeNames.InlineLazy);
		Fields = !string.IsNullOrEmpty(FieldsSetParam)
			? BoardFieldConfig.FromKeys(FieldsParam)
			: savedPref?.Fields is { } savedFieldsCsv
				? BoardFieldConfig.FromKeys(savedFieldsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
				: DefaultFields;

		// kanban-column-picker: the SAME cascade as Fields just above — explicit `?columns=`+
		// `columnsSet=1` wins (and is what WRITES the preference below) -> the saved per-board DB
		// preference -> every column visible.
		VisibleColumns = !string.IsNullOrEmpty(ColumnsSetParam)
			? BoardColumnConfig.FromKeys(ColumnsParam, knownColumnSlugs)
			: savedPref?.Columns is { } savedColumnsCsv
				? BoardColumnConfig.FromKeys(savedColumnsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries), knownColumnSlugs)
				: BoardColumnConfig.AllVisible(knownColumnSlugs);

		// board-view-cross-device: `?view=`/`?fields=` are the explicit, shareable override AND the
		// thing that writes the DB preference for next time (and every other device) — a plain
		// read-modify-write against the ONE per-user ViewPreferences dictionary, skipped entirely
		// when nothing actually changed (repeat-loading the same saved link shouldn't fire a write
		// every time) or when there's no authenticated user to own the row.
		if (userIdString is not null)
		{
			var nextPref = savedPref ?? new BoardViewPreference();
			var changed = false;
			if (ViewMode is not null && (nextPref.Mode != ViewMode || nextPref.By != By))
			{
				nextPref = nextPref with { Mode = ViewMode, By = By };
				changed = true;
			}
			if (!string.IsNullOrEmpty(FieldsSetParam))
			{
				var fieldsCsv = Fields.ToCsv();
				if (nextPref.Fields != fieldsCsv)
				{
					nextPref = nextPref with { Fields = fieldsCsv };
					changed = true;
				}
			}
			if (!string.IsNullOrEmpty(ColumnsSetParam))
			{
				var columnsCsv = VisibleColumns.ToCsv(knownColumnSlugs);
				if (nextPref.Columns != columnsCsv)
				{
					nextPref = nextPref with { Columns = columnsCsv };
					changed = true;
				}
			}
			if (DecisionPendingParam is not null && nextPref.DecisionPendingOnly != DecisionPendingParam)
			{
				nextPref = nextPref with { DecisionPendingOnly = DecisionPendingParam };
				changed = true;
			}
			if (changed)
			{
				var updatedPrefs = new Dictionary<string, BoardViewPreference>(boardPrefs.ViewPreferences, StringComparer.Ordinal)
				{
					[boardPrefKey] = nextPref,
				};
				var updatedBoardPrefs = boardPrefs with { ViewPreferences = updatedPrefs };
				var updatedBy = long.TryParse(userIdString, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var uid) ? uid : (long?)null;
				await _settings.SetAsync(Scope.User, userIdString, updatedBoardPrefs, boardPrefs, updatedBy, ct);
			}
		}

		// board-read-loads-all-bodies (board-page-cost): Body is the fattest column per node —
		// skip it in the DB read entirely when nothing on THIS render needs it. Outline is NOT the
		// same rule as every other mode: even with Fields.Body off, _BoardViewOutline.cshtml still
		// computes `hasBody = !string.IsNullOrEmpty(n.Body)` per node to decide "lazy" (offer a
		// per-node expand affordance — only meaningful for a node that actually HAS a body) vs the
		// plain no-affordance row — so outline needs the REAL body whenever it isn't in navigate
		// reveal, eager or lazy alike (this matches its pre-existing behavior exactly: GetAsync
		// always loaded every body regardless of Fields.Body — see that partial's own
		// "board-read-loads-all-bodies" comment — only the HTML emission was gated). Only navigate
		// reveal (the heading always links out, hasBody is never consulted) and every OTHER view
		// mode (Tree/Tags/Kanban/Table, which gate purely on Fields.Body with no hasBody-dependent
		// branch) can skip the load. Both Fields and OutlineRevealMode are already resolved above.
		var outlineNavigates = string.Equals(OutlineRevealMode, OutlineRevealModeNames.Navigate, StringComparison.Ordinal);
		var needsBody = string.Equals(ResolvedViewMode, BoardViewModeNames.Outline, StringComparison.OrdinalIgnoreCase)
			? !outlineNavigates
			: Fields.Body;

		// includeClosed: we render closed nodes too (the "active only" toggle hides them
		// client-side, and now also server-side via ActiveOnly/Hidden — see TaskNodeCard.Hidden);
		// GetAsync supplies each node's part_of parent + depth.
		var view = await _tasks.GetAsync(ProjectKey, Board, includeClosed: true, includeBody: needsBody, ct: ct);
		var boardNodes = view.Nodes;

		// decision-pending-has-no-ui: narrowed through the SAME entity predicate tasks_search's
		// decisionPending param and the owner digest already ride (TaskNodeFilter.DecisionPending →
		// TaskSearchFilter.Apply) — a real `Where` over the selected pool, run and resolved to a
		// concrete NodeId set BEFORE OrderHierarchically ever sees the excluded rows, not a display-
		// only hide of cards the browser already received (board-filters-server-state's Hidden flag
		// is the wrong shape for THIS filter: an unchecked "waiting only" toggle must ship a board
		// the excluded rows never reached, because a shared `?decisionPending=true` link with zero
		// matches must render empty, not the full board with everything merely dimmed).
		// StatusKind widened to all three facets (the documented includeClosed:true replacement) so
		// a CLOSED node that still carries the flag (decision-pending-survives-closure) stays in the
		// queue exactly like this page's own unfiltered GetAsync(includeClosed:true) already does.
		if (DecisionPendingOnly)
		{
			var pending = await _tasks.SearchNodesAsync(ProjectKey, new SearchRequest<TaskNodeFilter, TaskSortBy>
			{
				Filter = new TaskNodeFilter(Board, StatusKind: ["open", "terminalok", "terminalcancel"], DecisionPending: true),
			}, ct: ct);
			var pendingIds = new HashSet<string>(pending.Hits.Select(h => h.Node.NodeId), StringComparer.Ordinal);
			boardNodes = [.. boardNodes.Where(n => pendingIds.Contains(n.NodeId))];
		}

		Nodes = OrderHierarchically([.. boardNodes], Runtime, KindSlug, SortComparer(SortBy, SortDesc), out var keepVisible);
		ClosedWithActiveDescendant = keepVisible;
		_parentOf = Nodes.ToDictionary(n => n.NodeId, n => n.ParentNodeId, StringComparer.Ordinal);

		// live-verification finding: resolve every card's regression fixed-by target to a slug in
		// ONE batch — empty/cheap on every board that isn't kind `observation` (no node carries a
		// non-null Observation there, see TaskNodeView's own contract).
		ObservationFixedByLinks = await ObservationFixedByResolver.ResolveManyAsync(_tasks, ProjectKey, Nodes.Select(n => n.Observation), ct);

		// board-page-cost: comments are NEVER rendered on the board (a thread is a node-detail-page
		// affordance) — one aggregate COUNT query replaces what used to be a full board-wide
		// comment load (bodies, tags, markdown render) DFS-flattened into every card.
		CommentCounts = await _comments.CountForBoardAsync(ProjectKey, Board, ct);

		// Resolve `[[slug]]` mentions across every card body in ONE batch (node-ref-autolink), so
		// each card's renderer can link resolvable mentions — skipped entirely when this render
		// won't show any body (needsBody false: nothing to autolink into).
		if (needsBody)
		{
			var bodies = Nodes.Select(n => (string?)n.Body).ToList();
			NodeRefs = await NodeRefMap.BuildAsync(_tasks, WorkspaceKey, ProjectKey, bodies, ct);
			// Same shape for memory-entry keys (memory-key-mention-link): the whole page's candidate
			// keys resolve in one batch per container, never per card.
			MemoryRefs = await BuildMemoryRefsAsync(bodies, ct);
		}

		// Tag-groups projection: only when the RESOLVED mode is tags with a valid dimension
		// list. Bad/empty `by` silently falls back to the tree (the service would reject it)
		// — the view stays explicit, no implicit redirects.
		var dims = (effectiveBy ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (string.Equals(ResolvedViewMode, BoardViewModeNames.Tags, StringComparison.OrdinalIgnoreCase) && dims.Length > 0)
		{
			try
			{
				var grouped = await _tasks.GetGroupedAsync(ProjectKey, Board, dims, ct);
				var byKey = Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
				GroupDims = grouped.GroupBy;
				GroupRows = FlattenGroups(grouped.Groups, 0, byKey);
				IsTagView = true;
			}
			catch (ArgumentException) { /* invalid namespace → stay on the tree view */ }
		}
		// Content-pane dispatch: registry-driven, one lookup, no if-chain in the .cshtml. Tags is
		// special-cased because IsTagView already folds in the by-validity fallback above (a
		// resolved "tags" mode with a bad/empty `by` must still draw the tree, not a broken tags
		// pane) — every other resolved mode (kanban/outline/table/tree, and any future entry)
		// dispatches straight off its own registry entry; a resolved key the registry doesn't
		// actually carry (shouldn't happen — Resolve() only ever returns a renderable key) falls
		// back to tree defensively rather than a 500.
		ContentPartialName = string.Equals(ResolvedViewMode, BoardViewModeNames.Tags, StringComparison.OrdinalIgnoreCase)
			? (IsTagView ? BoardViewModeRegistry.Find(BoardViewModeNames.Tags) : BoardViewModeRegistry.Find(BoardViewModeNames.Tree))?.PartialName ?? "_BoardViewTree"
			: BoardViewModeRegistry.Find(ResolvedViewMode)?.PartialName ?? "_BoardViewTree";
		return Page();
	}

	// Depth-first flatten of the nested tag groups into header/card rows. A leaf group emits a
	// header then a card row per node (looked up by key); an inner group emits a header then
	// recurses its sub-groups one level deeper.
	static List<GroupRow> FlattenGroups(IReadOnlyList<TagGroup> groups, int depth, IReadOnlyDictionary<string, TaskNodeView> byKey)
	{
		var rows = new List<GroupRow>();
		foreach (var g in groups)
		{
			rows.Add(new GroupRow(depth, g.Key, g.Delivery, null));
			if (g.SubGroups.Count > 0)
				rows.AddRange(FlattenGroups(g.SubGroups, depth + 1, byKey));
			else
				foreach (var k in g.NodeKeys)
					if (byKey.TryGetValue(k, out var node))
						rows.Add(new GroupRow(depth + 1, null, null, node));
		}
		return rows;
	}

	// The board's effective process context: board-scoped MethodologyRuntime (instance
	// rules when membership is set) plus this board's stored kind slug. ListBoardsAsync
	// supplies the raw slug; this page must not open the store directly.
	async Task<(MethodologyRuntime Runtime, string? KindSlug, DateTime? ClosedAt, string? InstanceName)> ResolveProcessAsync(CancellationToken ct)
	{
		var meta = (await _tasks.ListBoardsAsync(ProjectKey, ct))
			.FirstOrDefault(b => string.Equals(b.Name, Board, StringComparison.Ordinal));
		var runtime = meta is null
			? await _tasks.GetRuntimeAsync(ProjectKey, ct)
			: await _tasks.GetRuntimeForBoardAsync(ProjectKey, Board, ct);
		return (runtime, meta?.Kind, meta?.ClosedAt, meta?.MethodologyInstance);
	}

	// Render order is the plan tree itself — DFS by part_of parent, siblings ordered by the
	// resolved sort comparator (default: Priority then Key — board-filters-server-state's
	// SortComparer reproduces this EXACT ordering for the default {"priority", desc:false} case, so
	// this is behaviorally unchanged for every caller that doesn't pass a different sort). A flat
	// priority sort let a low-priority child of an early branch visually drift past a later one
	// (finding D11). DFS keeps every node under its parent regardless of which key sorts siblings.
	static List<TaskNodeView> OrderHierarchically(
		List<TaskNodeView> nodes, MethodologyRuntime runtime, string? kindSlug, IComparer<TaskNodeView> sort,
		out IReadOnlySet<string> closedWithActiveDescendant)
	{
		var byId = new Dictionary<string, TaskNodeView>(StringComparer.Ordinal);
		foreach (var n in nodes) byId[n.NodeId] = n;

		// A node is a root when it has no part_of parent, or its parent isn't on this board.
		static string? ParentOf(TaskNodeView n) => n.ParentNodeId;

		var childMap = nodes
			.Where(n => ParentOf(n) is { } pid && byId.ContainsKey(pid))
			.GroupBy(n => ParentOf(n)!)
			.ToDictionary(
				g => g.Key,
				g => (IReadOnlyList<TaskNodeView>)g
					.OrderBy(n => n, sort)
					.ThenBy(n => n.Key, StringComparer.Ordinal)
					.ToList(),
				StringComparer.Ordinal);

		var roots = nodes
			.Where(n => { var pk = ParentOf(n); return pk is null || !byId.ContainsKey(pk); })
			.OrderBy(n => n, sort)
			.ThenBy(n => n.Key, StringComparer.Ordinal)
			.ToList();

		var ordered = new List<TaskNodeView>(nodes.Count);
		var closedKeep = new HashSet<string>(StringComparer.Ordinal);

		// Returns whether the subtree holds a non-closed node, so a closed parent of open
		// work stays visible. Guarded against part_of cycles via the visited set.
		var visited = new HashSet<string>(StringComparer.Ordinal);
		bool Emit(TaskNodeView node)
		{
			if (!visited.Add(node.NodeId)) return false;
			ordered.Add(node);
			var closed = runtime.IsTerminalStatus(kindSlug, node.Status);
			var hasActiveDescendant = false;
			if (childMap.TryGetValue(node.NodeId, out var kids))
				foreach (var kid in kids)
					hasActiveDescendant |= Emit(kid);
			if (closed && hasActiveDescendant) closedKeep.Add(node.NodeId);
			return !closed || hasActiveDescendant;
		}

		foreach (var r in roots) Emit(r);
		closedWithActiveDescendant = closedKeep;
		return ordered;
	}

	// Quick-add from the board UI: drops a new task into the `incoming` phase with an
	// auto-generated key. Status/type/NodeId are decided by the service (same path the
	// MCP upsert uses), so a kinded board gets a valid initial status, not a cold "Pending".
	public async Task<IActionResult> OnPostCreateAsync(string name, string? body, long priority, CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey, WorkspaceRole.Member)) return Forbid();
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _tasks.BoardExistsAsync(ProjectKey, Board, ct)) return NotFound();

		var (runtime, kindSlug, closedAt, _) = await ResolveProcessAsync(ct);
		if (!runtime.QuickAddAllowed(kindSlug) || closedAt is not null) return BadRequest();

		await _tasks.QuickAddAsync(ProjectKey, Board, name, body, priority, ct);

		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}
}
