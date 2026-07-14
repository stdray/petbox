using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.ProjectHome;

// kanban-column-picker: the column (status) visibility dialog's own model — the KANBAN-ONLY
// twin of BoardFilterSortModel/_BoardFieldsDialog (spec board-view-fields' sibling). Kanban-only
// by design (never rendered in tree/table/outline), so this doesn't extend the shared
// BoardFilterSortModel every mode gets — Columns/Visible are this board's OWN workflow-status
// vocabulary (TaskBoardModel.KanbanColumns), never a fixed field list like BoardFieldNames.
public sealed record BoardColumnsDialogModel(
	IReadOnlyList<TaskBoardModel.KanbanColumn> Columns,
	BoardColumnConfig Visible,
	string ViewMode,
	string? By);
