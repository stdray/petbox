using PetBox.Tasks.Workflow;

namespace PetBox.Web.Pages.Shared;

// The ONE status-badge rule, shared by the board list card (_PlanNodeCard) and the node detail
// page (TaskBoardNode) so the two can never disagree on whether a status shows
// (ui-spec-status-board-node-mismatch). Given the board's effective kind (resolved through
// MethodologyRuntime) and a status slug it answers both questions: SHOULD the badge render, and
// with WHICH daisyUI colour. Presentation only — the domain owns StatusKind/terminality.
public sealed record StatusBadgeModel(MethodologyRuntime Runtime, string? KindSlug, string Status)
{
	// Spec PRESET boards suppress the status badge for every non-terminal status: on a spec board
	// `defined` is the ~universal default → pure noise, so a badge shows only for a non-default
	// (terminal `deprecated`) state (spec-board-status-noise #9). Every other board — and a defined
	// custom kind, for which PresetKind is null — always shows the status.
	public bool Show =>
		Runtime.PresetKind(KindSlug) != BoardKind.Spec || Runtime.IsTerminalStatus(KindSlug, Status);

	// StatusKind → daisyUI badge class. The kind is resolved through the runtime per the board's
	// effective kind, so a definition-declared custom status colours right.
	public string CssClass => Classify(Runtime.StatusKindOf(KindSlug, Status));

	static string Classify(StatusKind? kind) => kind switch
	{
		StatusKind.TerminalOk => "badge-success",
		StatusKind.TerminalCancel => "badge-ghost",
		StatusKind.Open => "badge-info",
		_ => "badge-neutral",
	};
}
