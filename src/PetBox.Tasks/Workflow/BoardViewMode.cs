namespace PetBox.Tasks.Workflow;

// The named view modes a board may render (spec board-view-modes): interchangeable ways
// to present the SAME set of plan nodes — switching never touches data (board-view-modes),
// only which mode is shown (board-view-persistence). This is the vocabulary
// MethodologyKindDef.DefaultView draws from (spec methodology-default-view-field).
//
// Lives in PetBox.Tasks, not PetBox.Web, because MethodologyDefinitionValidator needs the
// name set and Tasks has no dependency on Web (layering runs the other way). The RENDERER
// for each mode (a Razor partial, picking a view-mode key -> partial-view mapping) lives in
// PetBox.Web's BoardViewModeRegistry — this type is names-only, no rendering.
public static class BoardViewModeNames
{
	// The stored part_of tree, DFS-ordered (the historical/only mode before this spec).
	public const string Tree = "tree";
	// The tag-groups projection over the same nodes (board-tag-grouping) — a pure view,
	// never mutates part_of (tag-grouping-is-projection). Needs an ordered `by` dimension
	// list to actually group; an invalid/missing one silently falls back to Tree.
	public const string Tags = "tags";
	// Cards grouped by workflow-status stage (board-view-mode-framework hands this to the
	// next worker — reserved here, no renderer yet).
	public const string Kanban = "kanban";
	// A hierarchical table-of-contents view (reserved, no renderer yet).
	public const string Outline = "outline";
	// A flat tabular view (reserved, no renderer yet).
	public const string Table = "table";

	// Every mode name the methodology layer may reference in `defaultView`. Kanban/outline/
	// table are listed even though PetBox.Web has not shipped a partial for them yet, so a
	// preset or user definition naming them passes validation now (the built-in quartet
	// presets already do — work/spec/intake/ideas) and only needs a renderer later; until
	// then BoardViewModeRegistry degrades an unrenderable-but-known mode to Tree at resolve
	// time (never a validation error, never a 500).
	public static readonly IReadOnlyList<string> All = [Tree, Tags, Kanban, Outline, Table];

	public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.OrdinalIgnoreCase);
}
