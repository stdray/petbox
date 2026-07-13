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
	// board-view-fields: populated only when Model.Fields opts them in — the caller (_BoardViewTable
	// for a board, Search for cross-scope) decides whether to resolve/pass these at all.
	IReadOnlyList<LinkDto>? BlockedBy = null,
	string? Body = null,
	// Populated only when ShowScopeColumns is true (cross-scope search) — a board-scoped table
	// leaves these null; the board itself already says where every row lives.
	string? Workspace = null, string? ProjectKey = null, string? Board = null);

// ShowScopeColumns=true renders workspace/project/board columns ahead of key (cross-scope
// search, where a row's location isn't implicit from the page it's on); false omits them
// (TaskBoard's own table view — the board IS the scope, repeating it on every row is noise).
// Fields=null (Search's cross-scope table) keeps the table's original always-on column set —
// board-view-fields' toggling is a board-page affordance, not a search-results one. WorkspaceKey/
// ProjectKey are the SINGLE board's scope (only meaningful — and only ever read — when Fields is
// non-null: the BlockedBy column's link routing; a cross-scope search row already carries its own
// per-row Workspace/ProjectKey and never opts BlockedBy in).
public sealed record TaskTableModel(
	IReadOnlyList<TaskTableRow> Rows, bool ShowScopeColumns,
	PetBox.Web.Rendering.BoardFieldConfig? Fields = null,
	string? WorkspaceKey = null, string? ProjectKey = null);
