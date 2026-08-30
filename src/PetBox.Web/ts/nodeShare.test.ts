// node-share-scope-switch-orphans-live-links, CLIENT half. The defect was not a rendering bug: the
// modal MINTED a public, indefinite capability token when it was merely opened, and another on every
// scope change, while keeping only the newest one in a module variable. A share token is addressable
// only BY VALUE (there is no "list share links" verb, by design), so each overwritten token stayed
// live forever with nothing on the page able to name it — unrevocable, permanently.
//
// These tests therefore assert about REQUESTS and about which Revoke buttons exist, not about
// looks: "opening issues nothing", "picking a scope issues nothing", and — the invariant that makes
// the fix a fix rather than a delay — "every token that was ever minted still has a Revoke button of
// its own on screen". The markup below is the modal region of Pages/Shared/_NodeShareModal.cshtml
// reduced to what this module binds to (same posture as nodeEdit.test.ts); the C# NodeShareUiTests
// assert that the real Razor partial ships those hooks.

import { beforeEach, describe, expect, test } from "bun:test";
import { JSDOM } from "jsdom";
import { initNodeShare } from "./nodeShare";

const MARKUP = `
<button data-node-share-open data-scope="node" data-testid="node-share-open">Share</button>
<button data-node-share-open data-scope="comment" data-comment-id="c7" data-testid="comment-share">Share comment</button>
<dialog id="node-share-modal" data-testid="node-share-modal" data-project="p" data-board="b" data-node-id="n1">
	<h3 id="node-share-title">Share this node</h3>
	<div data-testid="node-share-scope-choice">
		<input type="radio" name="node-share-scope" value="body" checked data-testid="node-share-scope-body" />
		<input type="radio" name="node-share-scope" value="full" data-testid="node-share-scope-full" />
	</div>
	<button data-node-share-create data-testid="node-share-create">Create link</button>
	<span id="node-share-status" data-testid="node-share-status"></span>
	<div id="node-share-links" data-testid="node-share-links">
		<div data-node-share-placeholder data-testid="node-share-placeholder">
			<input type="text" readonly disabled data-testid="node-share-url" />
			<button disabled data-node-share-copy data-testid="node-share-copy">Copy</button>
			<button disabled data-node-share-revoke data-testid="node-share-revoke">Revoke</button>
		</div>
		<p data-node-share-empty data-testid="node-share-empty" style="display:none">No live links</p>
	</div>
	<template data-node-share-row-template>
		<div data-node-share-link>
			<input type="text" readonly data-node-share-url data-testid="node-share-link-url" />
			<button data-node-share-copy data-testid="node-share-link-copy">Copy</button>
			<button data-node-share-revoke data-testid="node-share-link-revoke">Revoke</button>
			<p data-node-share-meta data-testid="node-share-link-meta"></p>
		</div>
	</template>
</dialog>`;

interface Call {
	url: string;
	method: string;
	body: Record<string, string> | null;
}

let dom: JSDOM;
let calls: Call[];
let mintedCount: number;
let mintOk: boolean;
let revokeOk: boolean;
let confirmAnswer: boolean;

const flush = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

function q(sel: string): HTMLElement {
	const found = dom.window.document.querySelector<HTMLElement>(sel);
	if (!found) throw new Error(`missing ${sel}`);
	return found;
}

function rows(): HTMLElement[] {
	return Array.from(dom.window.document.querySelectorAll<HTMLElement>("[data-node-share-link]"));
}

function tokensOnScreen(): string[] {
	return rows().map((row) => row.dataset["token"] ?? "");
}

// Every Revoke button attached to a live link — the placeholder's inert one does not count, which is
// exactly the distinction the defect blurred.
function revokeButtons(): HTMLButtonElement[] {
	return rows().flatMap((row) => Array.from(row.querySelectorAll<HTMLButtonElement>("[data-node-share-revoke]")));
}

function mints(): Call[] {
	return calls.filter((c) => c.url === "/api/share/node");
}

function revokes(): Call[] {
	return calls.filter((c) => c.method === "DELETE");
}

const mockFetch = async (
	input: string,
	init?: { method?: string; body?: string },
): Promise<{ ok: boolean; status: number; json: () => Promise<unknown> }> => {
	const url = String(input);
	calls.push({
		url,
		method: init?.method ?? "GET",
		body: init?.body ? (JSON.parse(init.body) as Record<string, string>) : null,
	});
	if (url === "/api/share/node") {
		mintedCount++;
		const id = `tok${mintedCount}`;
		return { ok: mintOk, status: mintOk ? 200 : 500, json: async () => ({ id }) };
	}
	return { ok: revokeOk, status: revokeOk ? 200 : 500, json: async () => ({}) };
};

function setup(): void {
	dom = new JSDOM(`<!doctype html><body>${MARKUP}</body>`, { url: "https://example.test" });
	const g = globalThis as unknown as Record<string, unknown>;
	g["document"] = dom.window.document;
	g["window"] = dom.window;
	g["Event"] = dom.window.Event;
	g["HTMLElement"] = dom.window.HTMLElement;
	g["fetch"] = mockFetch as unknown as typeof globalThis.fetch;

	calls = [];
	mintedCount = 0;
	mintOk = true;
	revokeOk = true;
	confirmAnswer = true;
	dom.window.confirm = () => confirmAnswer;
	// jsdom's <dialog> support is not what is under test; the module only needs the call not to throw.
	const dialog = q("#node-share-modal") as HTMLDialogElement;
	if (typeof dialog.showModal !== "function") {
		(dialog as unknown as { showModal: () => void }).showModal = () => dialog.setAttribute("open", "");
	}

	initNodeShare();
}

async function open(sel = "[data-testid='node-share-open']"): Promise<void> {
	q(sel).click();
	await flush();
}

async function chooseScope(value: "body" | "full"): Promise<void> {
	const radio = q(`[data-testid='node-share-scope-${value}']`) as HTMLInputElement;
	radio.checked = true;
	radio.dispatchEvent(new dom.window.Event("change", { bubbles: true }));
	await flush();
}

async function create(): Promise<void> {
	q("[data-testid='node-share-create']").click();
	await flush();
}

describe("issuing is an explicit act", () => {
	beforeEach(setup);

	test("opening the dialog mints nothing", async () => {
		await open();

		expect(calls).toHaveLength(0);
		expect(rows()).toHaveLength(0);
		// and the controls that would act on a link are inert until one exists
		expect((q("[data-testid='node-share-url']") as HTMLInputElement).disabled).toBe(true);
		expect((q("[data-testid='node-share-copy']") as HTMLButtonElement).disabled).toBe(true);
		expect((q("[data-testid='node-share-revoke']") as HTMLButtonElement).disabled).toBe(true);
	});

	test("picking a scope before issuing mints nothing", async () => {
		await open();
		await chooseScope("full");
		await chooseScope("body");
		await chooseScope("full");

		expect(calls).toHaveLength(0);
	});

	test("opening the per-comment share mints nothing either", async () => {
		await open("[data-testid='comment-share']");

		expect(calls).toHaveLength(0);
	});

	test("Create link mints once, with the selected scope, and hands back a usable row", async () => {
		await open();
		await chooseScope("full");
		await create();

		expect(mints()).toHaveLength(1);
		expect(mints()[0]?.body).toEqual({ projectKey: "p", board: "b", nodeId: "n1", scope: "full" });
		expect(rows()).toHaveLength(1);
		expect((q("[data-testid='node-share-link-url']") as HTMLInputElement).value).toBe(
			"https://example.test/ui/share/node/tok1",
		);
		expect(revokeButtons()).toHaveLength(1);
		expect(revokeButtons()[0]?.disabled).toBe(false);
		// the inert preview is gone rather than left sitting under a real link
		expect(dom.window.document.querySelector("[data-node-share-placeholder]")).toBeNull();
	});

	test("the comment scope carries the comment id", async () => {
		await open("[data-testid='comment-share']");
		await create();

		expect(mints()[0]?.body).toEqual({
			projectKey: "p",
			board: "b",
			nodeId: "n1",
			scope: "comment",
			commentId: "c7",
		});
	});

	test("a failed mint adds no row and leaves Create usable", async () => {
		mintOk = false;
		await open();
		await create();

		expect(rows()).toHaveLength(0);
		expect((q("[data-testid='node-share-create']") as HTMLButtonElement).disabled).toBe(false);
		expect(q("[data-testid='node-share-status']").textContent).toContain("Failed");
	});
});

// THE regression the card is about: a second token must never take the first one's place, because
// the first is live, indefinite, and nameable only from this page.
describe("no minted token loses its Revoke", () => {
	beforeEach(setup);

	test("issuing a second scope keeps the first link, with its own Revoke", async () => {
		await open();
		await create();
		await chooseScope("full");
		await create();

		expect(mints()).toHaveLength(2);
		expect(tokensOnScreen().sort()).toEqual(["tok1", "tok2"]);
		expect(revokeButtons()).toHaveLength(2);
		for (const button of revokeButtons()) expect(button.disabled).toBe(false);
	});

	test("switching scope back and forth after issuing mints nothing more and drops nothing", async () => {
		await open();
		await create();
		await chooseScope("full");
		await chooseScope("body");

		expect(mints()).toHaveLength(1);
		expect(tokensOnScreen()).toEqual(["tok1"]);
	});

	test("reopening the dialog keeps the links already issued on this page", async () => {
		await open();
		await create();
		await open();

		expect(mints()).toHaveLength(1);
		expect(tokensOnScreen()).toEqual(["tok1"]);
		expect(revokeButtons()).toHaveLength(1);
	});

	test("pressing Create twice for the same scope does not mint a second secret", async () => {
		await open();
		await create();
		await create();

		expect(mints()).toHaveLength(1);
		expect(tokensOnScreen()).toEqual(["tok1"]);
		expect(q("[data-testid='node-share-status']").textContent).toContain("already exists");
	});
});

describe("revoking", () => {
	beforeEach(setup);

	test("revokes exactly the token of the row whose button was pressed", async () => {
		await open();
		await create();
		await chooseScope("full");
		await create();

		const first = rows().find((row) => row.dataset["token"] === "tok1");
		first?.querySelector<HTMLButtonElement>("[data-node-share-revoke]")?.click();
		await flush();

		expect(revokes()).toHaveLength(1);
		expect(revokes()[0]?.url).toBe("/api/share/tok1");
		expect(revokes()[0]?.body).toEqual({ projectKey: "p" });
		expect(tokensOnScreen()).toEqual(["tok2"]);
	});

	test("declining the confirmation revokes nothing", async () => {
		confirmAnswer = false;
		await open();
		await create();
		revokeButtons()[0]?.click();
		await flush();

		expect(revokes()).toHaveLength(0);
		expect(tokensOnScreen()).toEqual(["tok1"]);
	});

	// A row dropped on a DELETE that then failed would take a STILL-LIVE token off screen — the
	// same loss by a different route.
	test("a failed revoke keeps the row, so the live token stays revocable", async () => {
		revokeOk = false;
		await open();
		await create();
		revokeButtons()[0]?.click();
		await flush();

		expect(tokensOnScreen()).toEqual(["tok1"]);
		expect(revokeButtons()[0]?.disabled).toBe(false);
		expect(q("[data-testid='node-share-status']").textContent).toContain("Failed");
	});

	test("revoking the last link restores the empty state", async () => {
		await open();
		await create();
		revokeButtons()[0]?.click();
		await flush();

		expect(rows()).toHaveLength(0);
		expect(q("[data-testid='node-share-empty']").style.display).toBe("");
	});
});
