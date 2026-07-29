// Declarative confirmation for destructive form posts: a <form data-confirm="…"> submits
// only after the user confirms. Markup stays JS-free (the no-inline-JS invariant); one
// document-level listener covers every such form, present and future (htmx swaps included).
export function initConfirmForms(): void {
	document.addEventListener("submit", (evt) => {
		const form = evt.target;
		if (!(form instanceof HTMLFormElement)) return;
		const message = resolveConfirmMessage(form);
		if (message && !window.confirm(message)) evt.preventDefault();
	});
}

// A form's confirm message is either the static `data-confirm` text, or — when the message
// must name something that can change client-side after the page rendered (e.g. "Load preset
// as template": the destination preset is whatever a sibling <select> currently holds, which
// the user may have changed since load) — a `data-confirm-template` with a `{preset}`
// placeholder filled from the LIVE value of the field named by `data-confirm-field` at submit
// time. This is what methodology-ui-footgun-after-cho-2c69c7 needs: a stale render-time value
// baked into the message would misname the very thing the confirm exists to call out.
// Exported for direct unit-testing (confirm.test.ts) without wiring a global `document`.
export function resolveConfirmMessage(form: HTMLFormElement): string | undefined {
	const template = form.dataset["confirmTemplate"];
	if (!template) return form.dataset["confirm"];
	const fieldName = form.dataset["confirmField"];
	const field = fieldName ? form.elements.namedItem(fieldName) : null;
	return template.replaceAll("{preset}", fieldValue(field));
}

// The live text value of a named form control, resolved by tag rather than `instanceof
// HTMLSelectElement` — an instanceof check against the bare global class would compare against
// the WRONG realm's constructor for a form built via `new JSDOM(...)` in a test (jsdom's own
// window has its own HTMLSelectElement, distinct from any ambient global one), so it's tag-name
// duck-typing here on purpose, not an oversight.
function fieldValue(field: Element | RadioNodeList | null): string {
	if (field === null || !("tagName" in field)) return ""; // null, or a RadioNodeList (no single value)
	return field.tagName === "SELECT" || field.tagName === "INPUT" || field.tagName === "TEXTAREA"
		? (field as HTMLSelectElement | HTMLInputElement | HTMLTextAreaElement).value
		: "";
}
