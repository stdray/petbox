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
//     modal). Near-fullscreen box + native scroll at rest (scale 1 == "fit").
//   * Zoom (v2, work `figure-viewer-zoom-controls`) — a `.figure-view-zoom-layer` div lives
//     PERMANENTLY inside the dialog's content viewport (never leaves the dialog) and receives a
//     `translate(pan) scale(zoom)` transform; the held unit is moved in and out of THAT layer,
//     never transformed directly, so restoreHeld never has to strip zoom state back off before
//     putting the unit back in the page. Scale/pan reset to identity on every open AND on close.
//     ctrl+wheel zooms (bare wheel stays native scroll — `wheel` is only preventDefault()'d when
//     ctrlKey is set); +/-/0 zoom via keyboard (the native dialog holds focus, so a `keydown` on
//     the dialog itself is enough); dragging pans once zoomed past fit (pointer events, so mouse
//     and touch share one path). The content viewport switches from native `overflow: auto`
//     (scale 1, unchanged v1 behaviour for an over-tall figure) to `overflow: hidden` once zoomed
//     (drag-panning and native scroll fighting over the same gesture would be worse than either
//     alone).
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

// Zoom/pan state (v2). Scoped to whichever unit is currently held — reset to identity on every
// open AND on close, so nothing survives a round trip through the dialog. 1 == "fit" (the v1
// baseline: the svg's own max-width:100%/height:auto sizing, untouched).
const ZOOM_STEP = 1.25;
const ZOOM_MIN = 0.5;
const ZOOM_MAX = 8;
let zoomScale = 1;
let panX = 0;
let panY = 0;
let panning = false;
let panStartClientX = 0;
let panStartClientY = 0;
let panOriginX = 0;
let panOriginY = 0;

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

// Exported for a plain unit test — everything else zoom-related is entangled with the dialog
// (showModal, pointer capture) that jsdom can't run, so this pure boundary check is the one piece
// worth a bun test; the dialog mechanics belong in the E2E suite alongside open/close/restore.
export function clampZoom(scale: number): number {
	return Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, scale));
}

function applyZoomTransform(zoomLayer: HTMLElement, content: HTMLElement): void {
	zoomLayer.style.transform = `translate(${panX}px, ${panY}px) scale(${zoomScale})`;
	content.classList.toggle("figure-view-zoomed", zoomScale > 1);
}

function resetZoom(zoomLayer: HTMLElement, content: HTMLElement): void {
	zoomScale = 1;
	panX = 0;
	panY = 0;
	applyZoomTransform(zoomLayer, content);
}

function setZoom(scale: number, zoomLayer: HTMLElement, content: HTMLElement): void {
	zoomScale = clampZoom(scale);
	if (zoomScale <= 1) {
		// Nothing to pan once we're back at (or below) fit — drop any leftover offset rather than
		// leave the figure visibly off-center at scale 1.
		panX = 0;
		panY = 0;
	}
	applyZoomTransform(zoomLayer, content);
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
	closeBar.className = "flex items-center justify-end gap-2";

	const zoomOutBtn = document.createElement("button");
	zoomOutBtn.type = "button";
	zoomOutBtn.className = "btn btn-sm btn-ghost";
	zoomOutBtn.setAttribute("data-testid", "figure-viewer-zoom-out");
	zoomOutBtn.setAttribute("aria-label", "Zoom out");
	zoomOutBtn.title = "Zoom out (-)";
	zoomOutBtn.textContent = "−";

	const zoomResetBtn = document.createElement("button");
	zoomResetBtn.type = "button";
	zoomResetBtn.className = "btn btn-sm btn-ghost";
	zoomResetBtn.setAttribute("data-testid", "figure-viewer-zoom-reset");
	zoomResetBtn.setAttribute("aria-label", "Reset zoom to fit");
	zoomResetBtn.title = "Reset to fit (0)";
	zoomResetBtn.textContent = "Fit";

	const zoomInBtn = document.createElement("button");
	zoomInBtn.type = "button";
	zoomInBtn.className = "btn btn-sm btn-ghost";
	zoomInBtn.setAttribute("data-testid", "figure-viewer-zoom-in");
	zoomInBtn.setAttribute("aria-label", "Zoom in");
	zoomInBtn.title = "Zoom in (+)";
	zoomInBtn.textContent = "+";

	const closeForm = document.createElement("form");
	closeForm.method = "dialog";
	const closeBtn = document.createElement("button");
	closeBtn.className = "btn btn-sm btn-ghost";
	closeBtn.setAttribute("data-testid", "figure-viewer-close");
	closeBtn.setAttribute("aria-label", "Close");
	closeBtn.textContent = "✕";
	closeForm.append(closeBtn);
	closeBar.append(zoomOutBtn, zoomResetBtn, zoomInBtn, closeForm);

	const content = document.createElement("div");
	content.className = "figure-view-dialog-content";
	// The zoom layer is DIALOG-OWNED scaffolding that never leaves the dialog — only the held
	// unit moves in and out of it. That keeps zoom state off the unit itself, so restoreHeld never
	// needs to strip a transform back off before the unit rejoins the page.
	const zoomLayer = document.createElement("div");
	zoomLayer.className = "figure-view-zoom-layer";
	content.append(zoomLayer);

	const backdrop = document.createElement("form");
	backdrop.method = "dialog";
	backdrop.className = "modal-backdrop";
	const backdropBtn = document.createElement("button");
	backdropBtn.setAttribute("aria-label", "Close");
	backdrop.append(backdropBtn);

	box.append(closeBar, content);
	dialog.append(box, backdrop);

	zoomOutBtn.addEventListener("click", () => setZoom(zoomScale / ZOOM_STEP, zoomLayer, content));
	zoomInBtn.addEventListener("click", () => setZoom(zoomScale * ZOOM_STEP, zoomLayer, content));
	zoomResetBtn.addEventListener("click", () => resetZoom(zoomLayer, content));

	// Bare wheel stays native scroll (content's overflow:auto at rest) — only ctrl+wheel is ours,
	// and only inside the dialog, so the page's own browser-zoom gesture is untouched everywhere
	// else. `{ passive: false }` is required for preventDefault() on wheel to actually take.
	content.addEventListener(
		"wheel",
		(event: WheelEvent) => {
			if (!event.ctrlKey) return;
			event.preventDefault();
			setZoom(zoomScale * (event.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP), zoomLayer, content);
		},
		{ passive: false },
	);

	// Drag-to-pan once zoomed past fit; pointer events so mouse and touch share one path.
	content.addEventListener("pointerdown", (event: PointerEvent) => {
		if (zoomScale <= 1) return;
		panning = true;
		panStartClientX = event.clientX;
		panStartClientY = event.clientY;
		panOriginX = panX;
		panOriginY = panY;
		content.setPointerCapture(event.pointerId);
		content.classList.add("figure-view-panning");
	});
	content.addEventListener("pointermove", (event: PointerEvent) => {
		if (!panning) return;
		panX = panOriginX + (event.clientX - panStartClientX);
		panY = panOriginY + (event.clientY - panStartClientY);
		applyZoomTransform(zoomLayer, content);
	});
	const endPan = (): void => {
		panning = false;
		content.classList.remove("figure-view-panning");
	};
	content.addEventListener("pointerup", endPan);
	content.addEventListener("pointercancel", endPan);

	dialog.addEventListener("keydown", (event: KeyboardEvent) => {
		if (event.key === "+" || event.key === "=") {
			event.preventDefault();
			setZoom(zoomScale * ZOOM_STEP, zoomLayer, content);
		} else if (event.key === "-" || event.key === "_") {
			event.preventDefault();
			setZoom(zoomScale / ZOOM_STEP, zoomLayer, content);
		} else if (event.key === "0") {
			event.preventDefault();
			resetZoom(zoomLayer, content);
		}
	});

	// `close` fires on every exit path — the close button, the backdrop form, Escape — so the
	// restore (and the zoom reset, so nothing survives a round trip) lives here and nowhere else.
	dialog.addEventListener("close", () => {
		restoreHeld();
		panning = false;
		resetZoom(zoomLayer, content);
	});
	document.body.append(dialog);
	return dialog;
}

function openUnit(unit: HTMLElement): void {
	if (findDialog()?.open) return; // already viewing something — never re-parent mid-view
	const dialog = ensureDialog();
	const zoomLayer = dialog.querySelector<HTMLElement>(".figure-view-zoom-layer");
	const content = dialog.querySelector<HTMLElement>(".figure-view-dialog-content");
	const parent = unit.parentNode;
	if (!zoomLayer || !content || !parent) return;
	held = { unit, parent, next: unit.nextSibling };
	resetZoom(zoomLayer, content);
	zoomLayer.replaceChildren(unit);
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
