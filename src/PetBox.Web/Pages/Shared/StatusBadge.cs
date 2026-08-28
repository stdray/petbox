using PetBox.Tasks.Workflow;

namespace PetBox.Web.Pages.Shared;

// The ONE status-badge rule, shared by the board list card (_TaskNodeCard) and the node detail
// page (TaskBoardNode) so the two can never disagree on whether a status shows
// (ui-spec-status-board-node-mismatch). Given the board's effective kind (resolved through
// MethodologyRuntime) and a status slug it answers both questions: SHOULD the badge render, and
// with WHICH daisyUI colour. Presentation only — the domain owns StatusKind/terminality.
public sealed record StatusBadgeModel(MethodologyRuntime Runtime, string? KindSlug, string Status)
{
	// Spec boards suppress the status badge for every non-terminal status: on a spec board
	// `defined` is the ~universal default → pure noise, so a badge shows only for a non-default
	// (terminal `deprecated`) state (spec-board-status-noise #9). Every other board always shows
	// the status. A "spec board" is identified by DATA — a kind that carries a delivery roll-up
	// (DeliveryOf(...) is not null) — NOT PresetKind(...) == BoardKind.Spec (production regression,
	// 2026-07, presetkind-spec-blind-spot): PresetKind nulls out for any DEFINED kind, and a real
	// project's spec board is virtually always definition-resolved. The old guard read
	// `null != BoardKind.Spec` == true there, so `Show` was ALWAYS true regardless of terminality on
	// $system's real spec board — the noise suppression silently never fired.
	public bool Show =>
		Runtime.DeliveryOf(KindSlug) is null || Runtime.IsTerminalStatus(KindSlug, Status);

	// Human label for the badge — the stored slug resolved to its declared status Name via the
	// runtime (e.g. `InProgress` → "In progress", `defined` → "Defined"). Slug is unchanged; the
	// board card's data-status attribute still carries the slug for the client filter.
	public string Display => Runtime.StatusName(KindSlug, Status);

	// StatusKind → status-pill class. The kind is resolved through the runtime per the board's
	// effective kind, so a definition-declared custom status colours right.
	//
	// The INPUT is the point: this is the node's StatusKind — metadata, classified by the ONE
	// authority (MethodologyRuntime.StatusKindOf) — and never a word matched out of the node's
	// body or title. A body that says "this is Done" or "broken" cannot move the pill.
	//
	// The output moved from bare daisyUI colour classes to the design layer's semantic
	// outline+fill pairs (work `node-render-design-layer`, see ts/app.css): a pill now carries a
	// contour AND a tint of the same hue instead of a flat fill, and reads as one family with the
	// alert callouts that use the same four pairs.
	public string CssClass => Classify(Runtime.StatusKindOf(KindSlug, Status));

	static string Classify(StatusKind? kind) => kind switch
	{
		StatusKind.TerminalOk => "status-pill status-pill-live",
		// Terminal-negative, the one pair reserved for "this will not happen". It stays a quiet
		// tint rather than an alarm — the card's line-through (board-terminal-negative-visible)
		// already carries the weight of saying a node is dead.
		StatusKind.TerminalCancel => "status-pill status-pill-broken",
		StatusKind.Open => "status-pill status-pill-proposed",
		// An unclassifiable status gets the neutral pair rather than being forced into a semantic
		// one — an unknown state must not claim to mean something.
		_ => "status-pill status-pill-muted",
	};
}
