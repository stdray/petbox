// figure-viewer (spec `body-figure-inspectable`), decorator half. What a unit test CAN own here
// is the selection logic — which elements get a trigger, which don't, and that re-running the
// decorator never stacks buttons (it re-runs after every htmx settle). The open/close/restore
// mechanics around the native <dialog>.showModal() need a real browser (jsdom does not implement
// showModal) — those live in the E2E test (tests/PetBox.E2ETests/FigureViewerTests.cs).

import { beforeEach, describe, expect, test } from "bun:test";
import { JSDOM } from "jsdom";
import { decorateFigures } from "./figure-viewer";

const SVG = '<svg viewBox="0 0 40 20"><rect x="1" y="1" width="38" height="18"></rect></svg>';

// A page's markdown surfaces reduced to what the decorator sees: a figure-carried diagram, a
// bare top-level svg, and an svg OUTSIDE any .md-body (a workflow-viz graph, say — never ours).
const MARKUP = `
<div class="md-body" data-testid="body-a">
	<p>prose</p>
	<figure data-testid="fig">${SVG}<figcaption>caption</figcaption></figure>
	${SVG.replace("<svg ", '<svg data-testid="bare" ')}
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

	test("a figure-carried svg decorates the FIGURE — one trigger on it, no extra wrapper", () => {
		const [unit] = decorateFigures(dom.window.document);
		expect(unit?.getAttribute("data-testid")).toBe("fig");
		expect(unit?.tagName).toBe("FIGURE");
		expect(unit?.querySelectorAll("[data-testid='figure-viewer-trigger']").length).toBe(1);
		// one trigger per decorated unit: the figure's and the bare svg's, nothing more
		expect(count("[data-testid='figure-viewer-trigger']")).toBe(2);
		expect(unit?.querySelector("figcaption")?.textContent).toBe("caption");
	});

	test("a bare svg gets a positioning wrapper that becomes its unit", () => {
		decorateFigures(dom.window.document);
		const bare = dom.window.document.querySelector("[data-testid='bare']");
		const wrapper = bare?.parentElement;
		expect(wrapper?.hasAttribute("data-figure-view")).toBe(true);
		expect(wrapper?.querySelector("[data-testid='figure-viewer-trigger']")).not.toBeNull();
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
		expect(units.length).toBe(3); // fig, bare wrapper, body-b's figure
	});

	test("re-decoration is idempotent — no stacked triggers", () => {
		decorateFigures(dom.window.document);
		const first = count("[data-testid='figure-viewer-trigger']");
		expect(first).toBe(2);
		decorateFigures(dom.window.document);
		decorateFigures(dom.window.document);
		expect(count("[data-testid='figure-viewer-trigger']")).toBe(first);
	});

	test("a newly swapped-in md-body (htmx preview) is decorated by a later pass", () => {
		decorateFigures(dom.window.document);
		dom.window.document
			.querySelector("[data-testid='body-b']")
			?.insertAdjacentHTML("beforeend", `<div>${SVG.replace("<svg ", '<svg data-testid="swapped" ')}</div>`);
		const swapped = dom.window.document.querySelector("[data-testid='swapped']");
		expect(swapped?.closest("[data-figure-view]")).toBeNull();
		decorateFigures(dom.window.document);
		expect(swapped?.closest("[data-figure-view]")).not.toBeNull();
	});
});
