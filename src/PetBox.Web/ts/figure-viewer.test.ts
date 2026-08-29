// figure-viewer (spec `body-figure-inspectable`), decorator half. What a unit test CAN own here
// is the selection logic — which elements get a trigger, which don't, and that re-running the
// decorator never stacks buttons (it re-runs after every htmx settle). The open/close/restore
// mechanics around the native <dialog>.showModal() need a real browser (jsdom does not implement
// showModal) — those live in the E2E test (tests/PetBox.E2ETests/FigureViewerTests.cs).

import { beforeEach, describe, expect, test } from "bun:test";
import { JSDOM } from "jsdom";
import { clampZoom, decorateFigures } from "./figure-viewer";

const SVG = '<svg viewBox="0 0 40 20"><rect x="1" y="1" width="38" height="18"></rect></svg>';

// A page's markdown surfaces reduced to what the decorator sees: a figure-carried diagram, a bare
// top-level svg (e.g. a GFM alert title's decorative icon — observation
// `figure-viewer-wraps-non-figure-svg-icons`), and an svg OUTSIDE any .md-body (a workflow-viz
// graph, say — never ours).
const MARKUP = `
<div class="md-body" data-testid="body-a">
	<p>prose</p>
	<figure data-testid="fig">${SVG}<figcaption>caption</figcaption></figure>
	<p class="markdown-alert-title">${SVG.replace("<svg ", '<svg data-testid="bare" ')}WARNING</p>
</div>
<div class="md-body" data-testid="body-b"></div>
<div data-testid="not-md-body">${SVG.replace("<svg ", '<svg data-testid="outside" ')}</div>`;

let dom: JSDOM;

function setup(): void {
	dom = new JSDOM(`<!doctype html><body>${MARKUP}</body>`);
	const g = globalThis as unknown as Record<string, unknown>;
	g["document"] = dom.window.document;
	g["window"] = dom.window;
}

const count = (sel: string): number => dom.window.document.querySelectorAll(sel).length;

describe("figure decoration", () => {
	beforeEach(setup);

	test("a figure-carried svg decorates the FIGURE — one trigger, nothing else", () => {
		const [unit] = decorateFigures(dom.window.document);
		expect(unit?.getAttribute("data-testid")).toBe("fig");
		expect(unit?.tagName).toBe("FIGURE");
		expect(unit?.querySelectorAll("[data-testid='figure-viewer-trigger']").length).toBe(1);
		// exactly one trigger on the whole page: the figure's — the alert-title icon gets none.
		expect(count("[data-testid='figure-viewer-trigger']")).toBe(1);
		expect(unit?.querySelector("figcaption")?.textContent).toBe("caption");
	});

	test("a bare svg with no <figure> ancestor gets nothing, even inside .md-body — e.g. a GFM alert title icon", () => {
		decorateFigures(dom.window.document);
		const bare = dom.window.document.querySelector("[data-testid='bare']");
		expect(bare?.closest("[data-figure-view]")).toBeNull();
		expect(bare?.parentElement?.querySelector("[data-testid='figure-viewer-trigger']")).toBeNull();
	});

	test("an svg outside .md-body gets nothing", () => {
		decorateFigures(dom.window.document);
		expect(dom.window.document.querySelector("[data-testid='outside']")?.hasAttribute("data-figure-view")).toBe(false);
	});

	test("every .md-body is covered, not just the first", () => {
		decorateFigures(dom.window.document);
		expect(dom.window.document.querySelector("[data-testid='body-b']")?.hasAttribute("data-figure-view")).toBe(false);
		// body-b is empty here — assert via a second body that DOES carry a figure
		dom.window.document
			.querySelector("[data-testid='body-b']")
			?.insertAdjacentHTML("beforeend", `<figure>${SVG}</figure>`);
		decorateFigures(dom.window.document);
		const units = dom.window.document.querySelectorAll("[data-figure-view]");
		expect(units.length).toBe(2); // body-a's fig, body-b's figure — the bare alert icon is not one
	});

	test("re-decoration is idempotent — no stacked triggers", () => {
		decorateFigures(dom.window.document);
		const first = count("[data-testid='figure-viewer-trigger']");
		expect(first).toBe(1);
		decorateFigures(dom.window.document);
		decorateFigures(dom.window.document);
		expect(count("[data-testid='figure-viewer-trigger']")).toBe(first);
	});

	test("a newly swapped-in md-body (htmx preview) is decorated by a later pass", () => {
		decorateFigures(dom.window.document);
		dom.window.document
			.querySelector("[data-testid='body-b']")
			?.insertAdjacentHTML("beforeend", `<figure>${SVG.replace("<svg ", '<svg data-testid="swapped" ')}</figure>`);
		const swapped = dom.window.document.querySelector("[data-testid='swapped']");
		expect(swapped?.closest("[data-figure-view]")).toBeNull();
		decorateFigures(dom.window.document);
		expect(swapped?.closest("[data-figure-view]")).not.toBeNull();
	});
});

// Zoom (v2, work `figure-viewer-zoom-controls`). The dialog-bound mechanics (buttons, ctrl+wheel,
// drag-pan, keyboard, reset-on-close) need a real browser — showModal and pointer capture aren't
// implemented by jsdom — so those live in the E2E suite. clampZoom is the one pure piece.
describe("zoom clamping", () => {
	test("passes values inside [0.5, 8] through unchanged", () => {
		expect(clampZoom(1)).toBe(1);
		expect(clampZoom(2.5)).toBe(2.5);
	});

	test("clamps below the minimum", () => {
		expect(clampZoom(0.1)).toBe(0.5);
	});

	test("clamps above the maximum", () => {
		expect(clampZoom(50)).toBe(8);
	});
});
