using PetBox.Tasks.Workflow;

namespace PetBox.Web.Rendering;

// One entry per board view mode TaskBoard.cshtml can actually RENDER (spec board-view-modes):
// a mode key, its switcher label, and the shared partial that draws its content pane. Adding
// a new mode (kanban/outline/table — reserved by BoardViewModeNames, no partial here yet) is
// ONE new entry + ONE new partial under Pages/ProjectHome/ — TaskBoard.cshtml/.cs dispatch off
// this list, never an if-branch per kind or per mode (board-view-mode-framework's core ask:
// ONE parameterized implementation shared by every board kind, not a copy per kind). Every
// partial receives the SAME TaskBoardModel and reads whatever it needs (Nodes for tree,
// GroupRows for tags, …) — see _BoardViewTree.cshtml / _BoardViewTags.cshtml for the shape a
// new partial should follow.
public sealed record BoardViewModeEntry(string Key, string Label, string PartialName);

public static class BoardViewModeRegistry
{
	// Extension point for the next worker: append an entry here (Key = a BoardViewModeNames
	// constant, PartialName = a new _BoardView*.cshtml under Pages/ProjectHome/) and the mode
	// picks up the switcher button, resolution, and defaultView wiring automatically — no
	// other file needs to change. Order here is the switcher's left-to-right order.
	public static readonly IReadOnlyList<BoardViewModeEntry> Entries =
	[
		new(BoardViewModeNames.Tree, "part_of", "_BoardViewTree"),
		new(BoardViewModeNames.Tags, "tags", "_BoardViewTags"),
	];

	public static BoardViewModeEntry? Find(string? key) =>
		key is null ? null : Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

	public static bool IsRenderable(string? key) => Find(key) is not null;

	// Resolution order (board-view-persistence): explicit choice (query-param, or a saved
	// localStorage pick promoted to a query-param by the client redirect — see
	// TaskBoard.cshtml's board-view-meta script) -> the methodology's defaultView for this
	// board's kind -> the builtin default (Tree). A candidate that isn't RENDERABLE yet
	// (unknown name, or a known-but-not-yet-implemented mode like kanban) is skipped
	// silently at every tier — never a 500, never a blank page.
	public static string Resolve(string? requested, string? methodologyDefault) =>
		Find(requested)?.Key
		?? Find(methodologyDefault)?.Key
		?? BoardViewModeNames.Tree;
}
