using PetBox.Tasks.Contract;

namespace PetBox.Web.Pages.Shared;

// One row of the reusable flat task table (_TaskTable.cshtml — board-view-mode-framework's
// table task): a pre-resolved projection, never a raw domain type. The two callers each own a
// different notion of "runtime" — TaskBoard has ONE MethodologyRuntime for the whole page;
// cross-scope search fans out over MANY projects, each with its own — so the partial never
// takes a Runtime itself. The caller resolves Status/Delivery presentation (colour, display
// name, terminality) with whatever runtime it has in hand and hands over the RESULT; the
// partial only lays out what it's given.
public sealed record TaskTableRow(
	string NodeId, string Key, string Title, string Url, string Type,
	string StatusSlug, string StatusDisplay, string StatusCssClass, bool StatusShow, bool Closed,
	long Priority, IReadOnlyList<string> Tags, DateTime? CreatedAt, DateTime? UpdatedAt,
	string? Delivery,
	// board-terminal-negative-visible: distinct from Closed (any terminal status) — this is
	// specifically StatusKind.TerminalCancel, the strikethrough invariant's trigger. Closed still
	// drives active-only filtering; TerminalCancel drives ONLY the title's line-through.
	bool TerminalCancel = false,
	// board-view-fields: populated only when Model.Fields opts it in — the caller (_BoardViewTable
	// for a board, Search for cross-scope) decides whether to resolve/pass it at all.
	IReadOnlyList<LinkDto>? BlockedBy = null,
	// Populated only when ShowScopeColumns is true (cross-scope search) — a board-scoped table
	// leaves these null; the board itself already says where every row lives.
	string? Workspace = null, string? ProjectKey = null, string? Board = null,
	// board-filters-server-state: server-computed active-only hide, matching TaskNodeCard.Hidden's
	// tree-view counterpart — an inline `display:none` on the <tr> so the first response already
	// shows the filtered table (no post-load hide/reflow). Default false: Search's cross-scope table
	// never sets it (search has no active-only concept), so its rows are unaffected.
	bool Hidden = false,
	// decision-pending-has-no-ui: the badge trigger for this row (_TaskTable.cshtml) — always
	// rendered when true, not gated on Fields (same posture as the tree card's own badge). Default
	// false: Search's cross-scope ToRow doesn't resolve it (out of this card's scope — see
	// CrossScopeSearchHit), so its rows never show the badge.
	bool DecisionPending = false,
	// observation-ui-distinct-from-task: the recurrence/regression signal (spec
	// observation-recurrence-visible-on-card / observation-regression-signalled-on-card), rendered
	// via the shared _ObservationRecurrenceBadge / _ObservationRegressionBanner partials — always
	// null except on the `observations` board (TaskNodeView's own contract), so every OTHER row is
	// unaffected. Default null: Search's cross-scope ToRow doesn't resolve it (out of scope, same
	// posture as BlockedBy above), so its rows never show these.
	ObservationSignalView? Observation = null,
	// node-session-provenance-visible-in-ui: TaskNodeView's own OriginSessionId/OriginSessions,
	// threaded through for the shared _NodeSessionProvenanceBadge partial — same posture as
	// Observation just above (every board's row carries it; Search's cross-scope ToRow doesn't
	// resolve it, out of scope here). "" default matches TaskNodeView.OriginSessionId's own
	// write-once "" = none-recorded default.
	string OriginSessionId = "", IReadOnlyList<string>? OriginSessions = null);

// ShowScopeColumns=true renders workspace/project/board columns ahead of key (cross-scope
// search, where a row's location isn't implicit from the page it's on); false omits them
// (TaskBoard's own table view — the board IS the scope, repeating it on every row is noise).
// Fields=null (Search's cross-scope table) keeps the table's original always-on column set —
// board-view-fields' toggling is a board-page affordance, not a search-results one. WorkspaceKey/
// ProjectKey are the SINGLE board's scope (only meaningful — and only ever read — when Fields is
// non-null: the BlockedBy column's link routing; a cross-scope search row already carries its own
// per-row Workspace/ProjectKey and never opts BlockedBy in).
// board-filters-server-state: ActiveOnly/SortBy/SortDesc default to BrowserState's own record
// defaults (true/"priority"/false) — Search doesn't resolve or pass these (its active-only/sort
// controls are session-only now, not persisted; see _BoardViewTable.cshtml's own comment for why
// that's an accepted, deliberately out-of-scope-here degradation), so its table renders exactly the
// same default appearance a first-time board visitor gets.
// ui-search-group-by-project: ShowFilterBar lets a page render several _TaskTable instances (one
// per collapsible project section) while showing the shared filter+sort bar (_BoardFilterSort)
// only ONCE — ts/board.ts's initBoardPage() already looks up each filter control via a single
// document.querySelector, so more than one bar in the DOM would leave every copy but the first
// dead. Each table still renders its OWN `[data-sort-scope]` tbody, so the ONE shared sort control
// reorders every section independently — the same shape kanban's per-column tbodies already prove
// board.ts supports unmodified. Defaults true: every existing single-table caller (TaskBoard's own
// table view, and Search's un-grouped exact-match table) is unaffected.
public sealed record TaskTableModel(
	IReadOnlyList<TaskTableRow> Rows, bool ShowScopeColumns,
	PetBox.Web.Rendering.BoardFieldConfig? Fields = null,
	string? WorkspaceKey = null, string? ProjectKey = null,
	bool ActiveOnly = true, string SortBy = "priority", bool SortDesc = false,
	bool ShowFilterBar = true,
	// decision-pending-has-no-ui: threaded through to the shared filter bar's toggle (TaskBoard's
	// own table view only — Search's cross-scope call never sets it, so its bar never shows the
	// toggle in the "on" state; the toggle link itself still renders there since the bar is shared,
	// but out-of-scope-here cross-project filtering isn't wired to it).
	bool DecisionPendingOnly = false,
	// live-verification finding: each row's Observation.FixedByNodeId, resolved to a slug once per
	// PAGE (TaskBoardModel.ObservationFixedByLinks) and threaded here so _TaskTable.cshtml never
	// resolves per-row. Null on Search's cross-scope table (Observation itself is never resolved
	// there either — see Observation's own comment above).
	IReadOnlyDictionary<string, LinkDto>? ObservationFixedByLinks = null,
	// recurrence-and-session-provenance-as-board-fields: whether THIS board's resolved kind is
	// `observation` (MethodologyRuntime.IsObservationKind) — _TaskTable.cshtml needs this ONE bit
	// to disable the Recurrence checkbox in its own _BoardFieldsDialog the same way BodyUnavailable
	// disables Body, and this partial (unlike the tree/kanban/outline callers) has no Runtime/
	// KindSlug of its own to ask. Default false: Search's cross-scope table never sets it — harmless,
	// since its ViewMode is always "" there and the dialog is skipped entirely (see _BoardFieldsDialog).
	bool IsObservationBoard = false);
