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
// Hidden = fully renderable/resolvable (Find/IsRenderable/Resolve see it exactly like any other
// entry — an explicit `?view=<key>` and a methodology defaultView of `<key>` both still work),
// but the SWITCHER doesn't offer a button for it — TaskBoard.cshtml filters the switcher's loop
// on this flag, so hiding a mode never means hardcoding its name in the .cshtml (board-tag-
// grouping-hidden): Tags is the first user (its presets worked poorly enough that showing them
// was actively confusing — 2026-07 maintainer call), pending a redesign. The grouping code
// itself (GetGroupedAsync, _BoardViewTags.cshtml, the `by` query param) stays fully wired; only
// the discovery affordance is gone.
public sealed record BoardViewModeEntry(string Key, string Label, string PartialName, bool Hidden = false);

public static class BoardViewModeRegistry
{
	// Extension point for the next worker: append an entry here (Key = a BoardViewModeNames
	// constant, PartialName = a new _BoardView*.cshtml under Pages/ProjectHome/) and the mode
	// picks up the switcher button, resolution, and defaultView wiring automatically — no
	// other file needs to change. Order here is the switcher's left-to-right order. All five
	// BoardViewModeNames are renderable as of board-view-mode-framework's follow-up
	// (kanban/outline/table shipped) — no name is reserved-but-unshipped anymore. Tags is
	// Hidden (see BoardViewModeEntry) — still fully renderable, just not offered in the switcher.
	public static readonly IReadOnlyList<BoardViewModeEntry> Entries =
	[
		new(BoardViewModeNames.Tree, "part_of", "_BoardViewTree"),
		new(BoardViewModeNames.Tags, "tags", "_BoardViewTags", Hidden: true),
		new(BoardViewModeNames.Kanban, "kanban", "_BoardViewKanban"),
		new(BoardViewModeNames.Outline, "outline", "_BoardViewOutline"),
		new(BoardViewModeNames.Table, "table", "_BoardViewTable"),
	];

	public static BoardViewModeEntry? Find(string? key) =>
		key is null ? null : Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

	public static bool IsRenderable(string? key) => Find(key) is not null;

	// Resolution order (board-view-persistence): explicit choice (query-param, or a saved
	// localStorage pick promoted to a query-param by the client redirect — see
	// TaskBoard.cshtml's board-view-meta script) -> the methodology's defaultView for this
	// board's kind -> the builtin default (Tree). A candidate that isn't RENDERABLE (an unknown
	// name, or a future BoardViewModeNames addition that outruns this registry again) is skipped
	// silently at every tier — never a 500, never a blank page.
	public static string Resolve(string? requested, string? methodologyDefault) =>
		Find(requested)?.Key
		?? Find(methodologyDefault)?.Key
		?? BoardViewModeNames.Tree;
}
