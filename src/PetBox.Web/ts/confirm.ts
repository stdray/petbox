// Declarative confirmation for destructive form posts: a <form data-confirm="…"> submits
// only after the user confirms. Markup stays JS-free (the no-inline-JS invariant); one
// document-level listener covers every such form, present and future (htmx swaps included).
export function initConfirmForms(): void {
	document.addEventListener("submit", (evt) => {
		const form = evt.target;
		if (!(form instanceof HTMLFormElement)) return;
		const message = form.dataset["confirm"];
		if (message && !window.confirm(message)) evt.preventDefault();
	});
}
