using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.ProjectHome;

// board-view-fields: the outline row's secondary-field chips (type/priority/status/delivery/
// updated/tags/blockedBy), factored out of _BoardViewOutline.cshtml because that partial renders
// the SAME chip row from two branches (the lazy-reveal <summary> and the plain heading row) — one
// shared partial rather than two copies of the eight `@if (Model.Fields.X)` checks.
public sealed record OutlineFieldChipsModel(
	MethodologyRuntime Runtime, string? KindSlug, BoardFieldConfig Fields, PlanNodeView Node,
	string WorkspaceKey, string ProjectKey);
