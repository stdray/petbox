using PetBox.Tasks.Contract;

namespace PetBox.Web.Pages.Shared;

// The BlockedBy field's rendering rule (board-view-fields), shared by every card/row that opts
// it in (tree, kanban, table — outline stays heading-only by design, see _BoardViewOutline). One
// small partial rather than four copies of the LinkDto→URL routing TaskBoardNode's own relations
// panel already does inline.
public sealed record BlockedByChipsModel(string WorkspaceKey, string ProjectKey, IReadOnlyList<LinkDto>? Links);
