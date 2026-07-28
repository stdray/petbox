// Keeps the "Enable methodology" lead paragraph honest about the preset actually selected
// (ui-preset-lead-vs-select). The admin tasks page renders ONE lead per provisioning preset —
// `[data-preset-lead="<slug>"]` — with all but the select's initial value hidden; this swaps the
// visible one on `change`. Pure DOM, no server round-trip and no preset knowledge here: the slugs
// come from whatever the server rendered, so a new preset needs no change in this file.

// Show exactly the lead whose slug matches `value`. Exported for the unit test (no DOM lookup of
// its own, so it can be driven over a hand-built fragment).
export function applyPresetLead(leads: Iterable<HTMLElement>, value: string): void {
	for (const lead of leads) lead.hidden = lead.dataset["presetLead"] !== value;
}

export function initMethodologyPresetLead(): void {
	const select = document.querySelector<HTMLSelectElement>("[data-testid='methodology-preset-select']");
	const container = document.querySelector<HTMLElement>("[data-testid='methodology-preset-lead']");
	if (!select || !container) return;

	const leads = [...container.querySelectorAll<HTMLElement>("[data-preset-lead]")];
	if (leads.length === 0) return;

	const apply = (): void => applyPresetLead(leads, select.value);
	select.addEventListener("change", apply);
	// Re-assert on load: a browser restoring the select's value after a back/reload would otherwise
	// leave the server's first-preset lead showing next to a different selection.
	apply();
}
