namespace PetBox.Web.Pages.Shared;

// board-node-filter / board-sort: the ONE parameterized filter+sort control bar, shared by
// EVERY board view mode (tree/kanban/outline/table — tags is a projection over tree, not a
// render target of its own). A mode INCLUDES this partial and declares what's applicable via
// this record, instead of copy-pasting the <div> — same discipline BoardViewModeRegistry
// applies to the switcher and TaskTableModel applies to the table body: one implementation,
// per-caller parameters.
//
// The control set itself (text / status / type / active-only / sort-key / sort-direction) is
// identical everywhere — board-node-filter and board-sort promise filtering+sorting to the
// BOARD, not to a specific render, so every mode gets full parity by default. What a mode
// actually declares is the SORT semantics: `SortHint` names what "sort" reorders in ITS OWN
// rendering (tree/outline reorder sibling branches in place; kanban reorders cards within each
// `data-sort-scope` column; table reorders the flat row list) — the control markup and the
// underlying mechanism (ts/board.ts initBoardPage, keyed off `[data-node-id]` / `[data-sort-scope]`)
// stay one shared implementation; only the label changes so the affordance reads correctly per
// mode. A future mode with a dimension this bar doesn't cover (or that wants FEWER controls)
// extends this record with a new switch, not a new copy of the markup.
// board-view-fields: the SAME bar also hosts the "fields" dialog trigger + form (one shared
// affordance per page load, same posture as the sort/filter controls above — a mode declares its
// current Fields/ViewMode/By, the bar draws the SAME dialog markup for every mode). ViewMode/By are
// the hidden fields the dialog's GET form round-trips so applying a field selection doesn't also
// reset the view the user is currently looking at.
// board-view-outline-show-bodies: BodyUnavailable is Outline's own honesty signal — true only
// when that board's kind is OutlineRevealModeNames.Navigate, where the outline never renders a
// body inline (wiki-like boards would ship megabytes on load) regardless of the dialog checkbox.
// Every other caller (Kanban/Table/Tree) leaves it false; the dialog just disables the Body
// checkbox and says why instead of silently ignoring a selection the user can still make.
public sealed record BoardFilterSortModel(
	PetBox.Web.Rendering.BoardFieldConfig Fields,
	string ViewMode,
	string SortHint = "sort:",
	string? By = null,
	bool BodyUnavailable = false);
