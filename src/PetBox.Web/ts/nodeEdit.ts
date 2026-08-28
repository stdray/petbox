// Node detail page edit interactivity — read/edit toggle + markdown write/preview. Imperative
// (mirrors board.ts / config.ts: no inline JS in Razor). The status change is a plain POST form
// and needs no script; this only wires the in-place title+body editor:
//   - "edit" reveals the form (server-prefilled) and hides the read title + body,
//   - the write/preview tabs swap the textarea for the rendered markdown preview,
//   - "cancel" restores the read view.
// All writes still go through the form POST → ITasksService (edit-respects-guards); this is pure
// presentation. Display is toggled via inline style (like board.ts) so daisyUI's .card display
// rules can't beat a [hidden] attribute.
//
// editor-preview-renders-server-side: the preview is RENDERED BY THE SERVER now — this module no
// longer imports a markdown library at all. It used to call ts/markdown.ts (marked + DOMPurify), a
// second pipeline that diverged structurally from the server's Markdig one: `##` produced a bare
// <h2> with no `md-section` wrapper and `> [!NOTE]` an ordinary blockquote holding the literal
// text "[!NOTE]", so the author previewed something the save would not produce. That module is
// deleted; this file's whole job in the preview is now scheduling — WHEN to ask the server — while
// the request itself is declared in Razor (hx-post="?handler=Preview" on the hidden trigger span).

// Debounce for the in-flight typing case. The preview costs a network round-trip (the accepted
// price of having one pipeline instead of two), so a keystroke must not be a request: the timer
// restarts on every input and only the pause fires. Opening the preview tab bypasses it entirely —
// an explicit click is a request for the CURRENT text and should not sit for a third of a second.
const PREVIEW_DEBOUNCE_MS = 400;

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
	const trigger = formEl.querySelector<HTMLElement>("[data-testid='node-edit-preview-trigger']");
	const previewError = formEl.querySelector<HTMLElement>("[data-testid='node-edit-preview-error']");

	let debounce: ReturnType<typeof setTimeout> | undefined;
	// Whether the preview pane currently holds a render from a SUCCESSFUL response. Decides which
	// failure message is honest: "the render you are looking at is stale" vs "there is nothing to
	// look at" — the two are different situations and saying the wrong one is a lie either way.
	let hasRender = false;
	let previewOpen = false;

	const showPreviewError = (message: string): void => {
		if (!previewError) return;
		previewError.textContent = message;
		previewError.style.display = "";
	};

	const clearPreviewError = (): void => {
		if (!previewError) return;
		previewError.textContent = "";
		previewError.style.display = "none";
	};

	// Ask the server now. htmx owns the request (declared on the trigger span in Razor); this only
	// fires the custom event its hx-trigger listens for, so nothing here depends on window.htmx
	// being resolvable — with htmx absent the event is simply unheard and the pane stays as it was.
	const requestPreview = (): void => {
		if (debounce !== undefined) {
			clearTimeout(debounce);
			debounce = undefined;
		}
		trigger?.dispatchEvent(new CustomEvent("preview-refresh"));
	};

	const schedulePreview = (): void => {
		if (debounce !== undefined) clearTimeout(debounce);
		debounce = setTimeout(() => {
			debounce = undefined;
			trigger?.dispatchEvent(new CustomEvent("preview-refresh"));
		}, PREVIEW_DEBOUNCE_MS);
	};

	// htmx swaps ONLY on a successful response, so on a failure the pane still shows the last good
	// render (or nothing yet). Say which, rather than leaving stale HTML passing for current.
	trigger?.addEventListener("htmx:afterRequest", (event: Event) => {
		const detail = (event as CustomEvent<{ successful?: boolean; xhr?: { status?: number } }>).detail;
		if (detail?.successful) {
			hasRender = true;
			clearPreviewError();
			return;
		}
		const status = detail?.xhr?.status ?? 0;
		if (status === 403) {
			showPreviewError("Preview unavailable — you do not have permission to edit this node.");
		} else if (status === 0) {
			showPreviewError(
				hasRender
					? "Preview unavailable (offline) — showing the last successful render."
					: "Preview unavailable — no connection to the server.",
			);
		} else {
			showPreviewError(
				hasRender
					? `Preview failed (HTTP ${status}) — showing the last successful render.`
					: `Preview failed (HTTP ${status}).`,
			);
		}
	});

	const setPreview = (on: boolean): void => {
		if (!textarea || !preview || !writeTab || !previewTab) return;
		previewOpen = on;
		textarea.style.display = on ? "none" : "";
		preview.style.display = on ? "" : "none";
		writeTab.classList.toggle("tab-active", !on);
		previewTab.classList.toggle("tab-active", on);
		if (on) requestPreview();
	};

	const setEditing = (on: boolean): void => {
		formEl.style.display = on ? "" : "none";
		titleEl.style.display = on ? "none" : "";
		readBodyEl.style.display = on ? "none" : "";
		editBtnEl.style.display = on ? "none" : "";
		if (on) setPreview(false); // always reopen on the write tab
	};

	// Keep the preview current while the author keeps typing with the tab open — debounced, and
	// only while it is actually visible: typing on the write tab must cost no requests at all.
	textarea?.addEventListener("input", () => {
		if (previewOpen) schedulePreview();
	});

	editBtnEl.addEventListener("click", () => setEditing(true));
	cancelBtn?.addEventListener("click", () => setEditing(false));
	writeTab?.addEventListener("click", () => setPreview(false));
	previewTab?.addEventListener("click", () => setPreview(true));

	setEditing(false);
}
