// Figure viewer (spec `body-figure-inspectable`): a figure embedded in a body can be viewed
// enlarged. Every markdown surface (node body, comment, editor preview) renders through
// _MdBody.cshtml into a `.md-body` wrapper, so ONE client-side pass covers all three — no server
// change, no Razor change: the SVG is already a live inline element in the DOM.
//
// Mechanism:
//   * Decorator — finds every `<svg>` in a `.md-body` and gives its UNIT (the enclosing
//     `<figure>` when present, else a positioning wrapper created around the bare svg) a corner
//     ghost button "⛶", revealed on hover/focus. NOT click-on-figure: that affordance is
//     non-obvious (precedent node-action-row-affordance).
//   * Dialog — a single native `<dialog class="modal">` created lazily and appended to <body>
//     (daisyUI 4 modal works with showModal; free ESC/focus/backdrop — precedent: the share
//     modal). Near-fullscreen box + native scroll; no zoom/pan in v1.
//   * MOVE, never clone — MarkdownRenderer rewrites every id defined inside a rendered <svg>
//     with a per-render content-hash suffix, and url(#id)/href="#id" are document-local, so
//     moving the unit within the same document keeps every cross-reference working, while a
//     clone would duplicate the ids. The original parent + insertion point are recorded and
//     restored on the dialog's `close` event (which fires for the close button, the backdrop
//     form AND Escape alike).
//
// Re-decoration is needed after htmx swaps: the editor preview div starts empty and is filled
// client-side (editor-preview-renders-server-side), and comment partials can arrive via htmx too
// — `htmx:afterSettle` bubbles to <body>, where one listener re-runs the (idempotent) decorator.

const UNIT_SELECTOR = "[data-figure-view]";
const TRIGGER_SELECTOR = "[data-testid='figure-viewer-trigger']";

// The unit currently held by the dialog, with everything needed to put it back exactly where it
// came from. `next` is the node the unit sat BEFORE — inserting before it on close reproduces the
// original position even when the unit wasn't the parent's last child.
interface HeldUnit {
	readonly unit: HTMLElement;
	readonly parent: ParentNode;
	readonly next: ChildNode | null;
}

let held: HeldUnit | null = null;

// Which elements get a trigger, and what the unit is:
//   * an <svg> inside a <figure>  → the figure itself is the unit (caption travels with it);
//   * a bare <svg>                → wrapped in a positioning div first (the button needs a
//     positioned ancestor, and an svg's own box is an unreliable one);
//   * an <svg> outside .md-body   → nothing (the decorator is scoped to markdown surfaces).
// Idempotent: a unit already carrying data-figure-view is skipped, so re-running after every
// htmx settle never stacks triggers.
export function decorateFigures(root: ParentNode): readonly HTMLElement[] {
	const decorated: HTMLElement[] = [];
	for (const body of root.querySelectorAll<HTMLElement>(".md-body")) {
		for (const svg of body.querySelectorAll("svg")) {
			if (svg.closest(UNIT_SELECTOR)) continue;
			const figure = svg.closest("figure");
			const unit = figure ?? wrapBare(svg);
			decorateUnit(unit);
			decorated.push(unit);
		}
	}
	return decorated;
}

function wrapBare(svg: SVGElement): HTMLElement {
	const wrapper = document.createElement("div");
	wrapper.className = "figure-view";
	wrapper.setAttribute("data-figure-view", "");
	svg.replaceWith(wrapper);
	wrapper.append(svg);
	return wrapper;
}

function decorateUnit(unit: Element): void {
	unit.classList.add("figure-view");
	unit.setAttribute("data-figure-view", "");
	const trigger = document.createElement("button");
	trigger.type = "button";
	trigger.className = "figure-view-trigger";
	trigger.setAttribute("data-testid", "figure-viewer-trigger");
	trigger.title = "View enlarged";
	trigger.setAttribute("aria-label", "View figure enlarged");
	trigger.textContent = "⛶";
	unit.append(trigger);
}

function findDialog(): HTMLDialogElement | null {
	return document.querySelector<HTMLDialogElement>("[data-testid='figure-viewer-dialog']");
}

// Created once, on first open — the dialog belongs to the page, not to any one _MdBody partial
// (a partial can render many times per page; a dialog per render would multiply both).
function ensureDialog(): HTMLDialogElement {
	const existing = findDialog();
	if (existing) return existing;

	const dialog = document.createElement("dialog");
	dialog.className = "modal";
	dialog.setAttribute("data-testid", "figure-viewer-dialog");

	const box = document.createElement("div");
	box.className = "modal-box figure-view-dialog-box";
	const closeBar = document.createElement("div");
	closeBar.className = "flex justify-end";
	const closeForm = document.createElement("form");
	closeForm.method = "dialog";
	const closeBtn = document.createElement("button");
	closeBtn.className = "btn btn-sm btn-ghost";
	closeBtn.setAttribute("data-testid", "figure-viewer-close");
	closeBtn.setAttribute("aria-label", "Close");
	closeBtn.textContent = "✕";
	closeForm.append(closeBtn);
	closeBar.append(closeForm);

	const content = document.createElement("div");
	content.className = "figure-view-dialog-content";

	const backdrop = document.createElement("form");
	backdrop.method = "dialog";
	backdrop.className = "modal-backdrop";
	const backdropBtn = document.createElement("button");
	backdropBtn.setAttribute("aria-label", "Close");
	backdrop.append(backdropBtn);

	box.append(closeBar, content);
	dialog.append(box, backdrop);
	// `close` fires on every exit path — the close button, the backdrop form, Escape — so the
	// restore lives here and nowhere else.
	dialog.addEventListener("close", restoreHeld);
	document.body.append(dialog);
	return dialog;
}

function openUnit(unit: HTMLElement): void {
	if (findDialog()?.open) return; // already viewing something — never re-parent mid-view
	const dialog = ensureDialog();
	const content = dialog.querySelector<HTMLElement>(".figure-view-dialog-content");
	const parent = unit.parentNode;
	if (!content || !parent) return;
	held = { unit, parent, next: unit.nextSibling };
	content.replaceChildren(unit);
	dialog.showModal();
}

function restoreHeld(): void {
	const state = held;
	held = null;
	if (!state) return;
	// The recorded sibling may have been re-rendered away while the dialog was open (an htmx swap
	// of the surface the unit came from); fall back to appending rather than throwing NotFoundError.
	if (state.next && state.next.parentNode === state.parent) state.parent.insertBefore(state.unit, state.next);
	else state.parent.append(state.unit);
}

export function initFigureViewer(): void {
	decorateFigures(document);

	// One delegated listener covers every trigger on every surface, including ones added by later
	// htmx swaps (no per-button binding to lose or double-fire).
	document.body.addEventListener("click", (event: Event) => {
		if (!(event.target instanceof Element)) return;
		const trigger = event.target.closest<HTMLElement>(TRIGGER_SELECTOR);
		if (!trigger) return;
		const unit = trigger.closest<HTMLElement>(UNIT_SELECTOR);
		if (unit) openUnit(unit);
	});

	// Re-decorate after htmx settles new content in (editor preview, comment partials). The
	// decorator is idempotent, so every settle is cheap and safe.
	document.body.addEventListener("htmx:afterSettle", () => {
		decorateFigures(document);
	});
}
