using PetBox.Tasks.Workflow;

namespace PetBox.Web.Rendering;

// One entry per board view mode TaskBoard.cshtml can actually RENDER (spec board-view-modes):
// a mode key, its switcher label, and the shared partial that draws its content pane. Adding
// a new mode is ONE new entry + ONE new partial under Pages/ProjectHome/ — TaskBoard.cshtml/.cs
// dispatch off this list, never an if-branch per kind or per mode (board-view-mode-framework's
// core ask: ONE parameterized implementation shared by every board kind, not a copy per kind).
// Every partial receives the SAME TaskBoardModel and reads whatever it needs (Nodes for tree,
// GroupRows for tags, WorkflowBlocks for kanban's columns, OutlineRevealMode for outline, …) —
// see _BoardViewTree.cshtml / _BoardViewTags.cshtml for the shape a new partial should follow.
// DisabledReason = fully renderable in the abstract (a real partial exists, Find/IsRenderable see
// it like any other entry) but NOT SELECTABLE — the switcher renders its button VISIBLE and
// INACTIVE (disabled, with this string as the tooltip explaining why) instead of omitting it, and
// Resolve refuses to land ANY request (explicit `?view=<key>` or a methodology defaultView of
// `<key>`) on it, falling through to the next tier exactly as if the name were unknown. Tags is
// the only user today (board-tag-grouping-disabled, owner call 2026-07-14): "needs a rethink and
// delivers nothing today" — the button stays so the mode isn't erased from memory, but neither
// the switcher click nor a hand-typed `?view=tags&by=...` URL renders the grouping pane anymore.
// The grouping code itself (GetGroupedAsync, TaskBoardModel's IsTagView/GroupRows/FlattenGroups,
// _BoardViewTags.cshtml) stays fully wired and correct — it simply never runs, because
// ResolvedViewMode can no longer become "tags" (see Resolve below). Flip DisabledReason back to
// null to re-enable; nothing else needs to change.
public sealed record BoardViewModeEntry(string Key, string Label, string PartialName, string? DisabledReason = null);

public static class BoardViewModeRegistry
{
	// Extension point for the next worker: append an entry here (Key = a BoardViewModeNames
	// constant, PartialName = a new _BoardView*.cshtml under Pages/ProjectHome/) and the mode
	// picks up the switcher button, resolution, and defaultView wiring automatically — no
	// other file needs to change. Order here is the switcher's left-to-right order. All five
	// BoardViewModeNames are renderable as of board-view-mode-framework's follow-up
	// (kanban/outline/table shipped) — no name is reserved-but-unshipped anymore.
	public static readonly IReadOnlyList<BoardViewModeEntry> Entries =
	[
		new(BoardViewModeNames.Tree, "part_of", "_BoardViewTree"),
		new(BoardViewModeNames.Tags, "tags", "_BoardViewTags",
			DisabledReason: "Tag grouping needs a rethink — disabled for now, code stays for later"),
		new(BoardViewModeNames.Kanban, "kanban", "_BoardViewKanban"),
		new(BoardViewModeNames.Outline, "outline", "_BoardViewOutline"),
		new(BoardViewModeNames.Table, "table", "_BoardViewTable"),
	];

	public static BoardViewModeEntry? Find(string? key) =>
		key is null ? null : Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

	public static bool IsRenderable(string? key) => Find(key) is not null;

	// Selectable = renderable AND not disabled — the gate Resolve applies at EVERY tier (board-
	// tag-grouping-disabled). A caller that already has an entry in hand (e.g. the switcher's own
	// render loop, which must still draw a disabled button) checks entry.DisabledReason directly
	// instead of going through this — IsSelectable is for "would Resolve ever land a request here".
	public static bool IsSelectable(string? key) => Find(key) is { DisabledReason: null };

	// Resolution order (board-view-persistence): explicit choice (query-param, or a saved
	// localStorage pick promoted to a query-param by the client redirect — see
	// TaskBoard.cshtml's board-view-meta script) -> the methodology's defaultView for this
	// board's kind -> the builtin default (Tree). A candidate that isn't SELECTABLE — unknown
	// name, OR a real but DISABLED entry (board-tag-grouping-disabled) — is skipped silently at
	// every tier — never a 500, never a blank page, and never a disabled mode's content pane.
	public static string Resolve(string? requested, string? methodologyDefault) =>
		(IsSelectable(requested) ? Find(requested)!.Key : null)
		?? (IsSelectable(methodologyDefault) ? Find(methodologyDefault)!.Key : null)
		?? BoardViewModeNames.Tree;
}
