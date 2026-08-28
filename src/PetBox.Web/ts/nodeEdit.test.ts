// editor-preview-renders-server-side, CLIENT half. The preview's rendering moved to the server,
// so what is left on this side is scheduling and failure reporting — and both are exactly the
// things a "one pipeline" change can quietly get wrong:
//   * a preview per keystroke turns one round-trip per pause into one per character,
//   * htmx does NOT swap on a failed response, so without a notice the pane keeps showing a stale
//     render as if it were current — the author's trust in the preview is the whole point of the
//     card, and silently-stale is a different way to lose it.
// The request itself is declared in Razor (hx-post on the trigger span); these tests assert the
// custom event that fires it, which is the seam this module actually owns.

import { beforeEach, describe, expect, test } from "bun:test";
import { JSDOM } from "jsdom";
import { initNodeEdit } from "./nodeEdit";

const PREVIEW_DEBOUNCE_MS = 400;

// The editor region of Pages/ProjectHome/TaskBoardNode.cshtml, reduced to the elements this
// module binds to.
const MARKUP = `
<div data-testid="node-detail">
	<h1 data-testid="node-name">T</h1>
	<button data-testid="node-edit-toggle">edit</button>
	<form data-testid="node-edit-form" style="display:none">
		<a data-testid="node-edit-write-tab">write</a>
		<a data-testid="node-edit-preview-tab">preview</a>
		<textarea data-testid="node-edit-body"></textarea>
		<span hidden data-testid="node-edit-preview-trigger"></span>
		<div data-testid="node-edit-preview-error" style="display:none"></div>
		<div data-testid="node-edit-preview" style="display:none"></div>
		<button data-testid="node-edit-cancel">cancel</button>
	</form>
	<div data-testid="node-read-body">body</div>
</div>`;

let dom: JSDOM;
let refreshes: number;

function q(sel: string): HTMLElement {
	const el = dom.window.document.querySelector<HTMLElement>(sel);
	if (!el) throw new Error(`missing ${sel}`);
	return el;
}

function setup(): void {
	dom = new JSDOM(`<!doctype html><body>${MARKUP}</body>`);
	const g = globalThis as unknown as Record<string, unknown>;
	g["document"] = dom.window.document;
	g["window"] = dom.window;
	g["CustomEvent"] = dom.window.CustomEvent;
	g["Event"] = dom.window.Event;

	refreshes = 0;
	dom.window.document
		.querySelector("[data-testid='node-edit-preview-trigger']")
		?.addEventListener("preview-refresh", () => {
			refreshes++;
		});

	initNodeEdit();
	q("[data-testid='node-edit-toggle']").click();
}

function type(text: string): void {
	const ta = q("[data-testid='node-edit-body']") as HTMLTextAreaElement;
	ta.value = text;
	ta.dispatchEvent(new dom.window.Event("input"));
}

const wait = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));

// htmx's own post-request event, as this module consumes it.
function afterRequest(successful: boolean, status: number): void {
	q("[data-testid='node-edit-preview-trigger']").dispatchEvent(
		new dom.window.CustomEvent("htmx:afterRequest", { detail: { successful, xhr: { status } } }),
	);
}

describe("edit preview scheduling", () => {
	beforeEach(setup);

	test("opening the preview tab asks the server immediately", () => {
		expect(refreshes).toBe(0);
		q("[data-testid='node-edit-preview-tab']").click();
		expect(refreshes).toBe(1);
	});

	test("typing on the WRITE tab costs no requests at all", async () => {
		type("a");
		type("ab");
		await wait(PREVIEW_DEBOUNCE_MS + 100);
		expect(refreshes).toBe(0);
	});

	test("typing with the preview open is debounced into ONE request, not one per keystroke", async () => {
		q("[data-testid='node-edit-preview-tab']").click();
		expect(refreshes).toBe(1); // the tab-open render

		type("#");
		type("##");
		type("## a");
		type("## ab");
		expect(refreshes).toBe(1); // nothing extra has fired yet

		await wait(PREVIEW_DEBOUNCE_MS + 150);
		expect(refreshes).toBe(2); // four keystrokes, ONE additional round-trip
	});

	test("a pause between bursts renders each burst", async () => {
		q("[data-testid='node-edit-preview-tab']").click();
		type("a");
		await wait(PREVIEW_DEBOUNCE_MS + 150);
		expect(refreshes).toBe(2);

		type("ab");
		await wait(PREVIEW_DEBOUNCE_MS + 150);
		expect(refreshes).toBe(3);
	});

	test("switching back to write stops further requests", async () => {
		q("[data-testid='node-edit-preview-tab']").click();
		q("[data-testid='node-edit-write-tab']").click();
		const before = refreshes;
		type("more text");
		await wait(PREVIEW_DEBOUNCE_MS + 150);
		expect(refreshes).toBe(before);
	});
});

describe("edit preview failure reporting", () => {
	beforeEach(setup);

	test("a failed request with nothing rendered yet says there is no connection", () => {
		q("[data-testid='node-edit-preview-tab']").click();
		afterRequest(false, 0);

		const err = q("[data-testid='node-edit-preview-error']");
		expect(err.style.display).toBe("");
		expect(err.textContent).toContain("no connection");
	});

	test("a failed request AFTER a good render says the pane is showing the last one", () => {
		q("[data-testid='node-edit-preview-tab']").click();
		afterRequest(true, 200);
		type("more");
		afterRequest(false, 0);

		const err = q("[data-testid='node-edit-preview-error']");
		expect(err.style.display).toBe("");
		// The honest distinction: the author is looking at real HTML, just not the current text.
		expect(err.textContent).toContain("last successful render");
	});

	test("an HTTP error reports its status", () => {
		q("[data-testid='node-edit-preview-tab']").click();
		afterRequest(false, 500);
		expect(q("[data-testid='node-edit-preview-error']").textContent).toContain("500");
	});

	test("a permission failure is named as one, not as an outage", () => {
		q("[data-testid='node-edit-preview-tab']").click();
		afterRequest(false, 403);
		expect(q("[data-testid='node-edit-preview-error']").textContent).toContain("permission");
	});

	test("a later success clears the notice", () => {
		q("[data-testid='node-edit-preview-tab']").click();
		afterRequest(false, 0);
		expect(q("[data-testid='node-edit-preview-error']").style.display).toBe("");

		afterRequest(true, 200);
		const err = q("[data-testid='node-edit-preview-error']");
		expect(err.style.display).toBe("none");
		expect(err.textContent).toBe("");
	});
});
