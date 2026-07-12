namespace PetBox.Tasks.Workflow;

// Parameterizes the ONE outline partial (_BoardViewOutline.cshtml — board-view-mode-framework's
// outline task): what "expand a heading" does, per board. Two boards with very different body
// shapes both render through the same partial — only this switch differs:
//   - InlineLazy: the node body is fetched on demand (TaskBoardModel.OnGetNodeBodyAsync, an
//     htmx GET fired by the <details> `toggle` event) and injected in place — never shipped in
//     the initial page for every node. Fits a board like spec, where a body is one short
//     normative line: cheap to fetch, cheap to read inline.
//   - Navigate: expanding does nothing; the heading link goes straight to the node's own page.
//     Fits a wiki-like board with long bodies, where inline expansion would be an unreadable
//     wall of text dropped into a heading list.
//
// Lives in PetBox.Tasks, not PetBox.Web, because MethodologyKindDef.OutlineReveal (below) and
// MethodologyDefinitionValidator need the name set and Tasks has no dependency on Web (layering
// runs the other way) — same reasoning as BoardViewModeNames. The RENDERER stays in
// PetBox.Web's _BoardViewOutline.cshtml; this type is names-only, no rendering.
public static class OutlineRevealModeNames
{
	public const string InlineLazy = "inline-lazy";
	public const string Navigate = "navigate";

	public static readonly IReadOnlyList<string> All = [InlineLazy, Navigate];

	public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.OrdinalIgnoreCase);
}
