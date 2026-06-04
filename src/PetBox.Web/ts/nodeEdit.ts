// Node detail page edit interactivity — read/edit toggle + markdown write/preview. Imperative
// (mirrors board.ts / config.ts: no inline JS in Razor). The status change is a plain POST form
// and needs no script; this only wires the in-place title+body editor:
//   - "edit" reveals the form (server-prefilled) and hides the read title + body,
//   - the write/preview tabs swap the textarea for a live-rendered markdown preview,
//   - "cancel" restores the read view.
// All writes still go through the form POST → ITasksService (edit-respects-guards); this is pure
// presentation. Display is toggled via inline style (like board.ts) so daisyUI's .card display
// rules can't beat a [hidden] attribute.

import { renderMarkdown } from "./markdown";

export function initNodeEdit(): void {
	const root = document.querySelector<HTMLElement>("[data-testid='node-detail']");
	if (!root) return;

	const form = root.querySelector<HTMLFormElement>("[data-testid='node-edit-form']");
	const editBtn = root.querySelector<HTMLElement>("[data-testid='node-edit-toggle']");
	const title = root.querySelector<HTMLElement>("[data-testid='node-name']");
	const readBody = root.querySelector<HTMLElement>("[data-testid='node-read-body']");
	if (!form || !editBtn || !title || !readBody) return;
	// Re-bind as non-null: TS doesn't carry control-flow narrowing into the nested closures below.
	const formEl = form;
	const editBtnEl = editBtn;
	const titleEl = title;
	const readBodyEl = readBody;

	const cancelBtn = formEl.querySelector<HTMLElement>("[data-testid='node-edit-cancel']");
	const textarea = formEl.querySelector<HTMLTextAreaElement>("[data-testid='node-edit-body']");
	const writeTab = formEl.querySelector<HTMLElement>("[data-testid='node-edit-write-tab']");
	const previewTab = formEl.querySelector<HTMLElement>("[data-testid='node-edit-preview-tab']");
	const preview = formEl.querySelector<HTMLElement>("[data-testid='node-edit-preview']");

	const setPreview = (on: boolean): void => {
		if (!textarea || !preview || !writeTab || !previewTab) return;
		textarea.style.display = on ? "none" : "";
		preview.style.display = on ? "" : "none";
		writeTab.classList.toggle("tab-active", !on);
		previewTab.classList.toggle("tab-active", on);
		if (on) preview.innerHTML = renderMarkdown(textarea.value);
	};

	const setEditing = (on: boolean): void => {
		formEl.style.display = on ? "" : "none";
		titleEl.style.display = on ? "none" : "";
		readBodyEl.style.display = on ? "none" : "";
		editBtnEl.style.display = on ? "none" : "";
		if (on) setPreview(false); // always reopen on the write tab
	};

	editBtnEl.addEventListener("click", () => setEditing(true));
	cancelBtn?.addEventListener("click", () => setEditing(false));
	writeTab?.addEventListener("click", () => setPreview(false));
	previewTab?.addEventListener("click", () => setPreview(true));

	setEditing(false);
}
