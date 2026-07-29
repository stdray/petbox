// Declarative form-confirm (confirm.ts): a plain `data-confirm` message, and the
// `data-confirm-template` + `data-confirm-field` variant methodology-ui-footgun-after-cho-2c69c7
// needs — a message that must name whatever a sibling <select> CURRENTLY holds at submit time,
// not whatever it held when the page rendered (the user may have changed it since). Driven over
// jsdom-built HTMLFormElements against the exported PURE `resolveConfirmMessage` — the same
// direct-function-test shape as methodology-preset.test.ts, and deliberately not through
// `initConfirmForms`'s document-level listener: that would need global `document`/`window`/
// `HTMLFormElement` stubs standing in for jsdom's own per-realm classes, for no extra coverage
// (initConfirmForms is three lines of glue around resolveConfirmMessage).
//
// Run: node --test ts/confirm.test.ts   (Node >= 23.6 native TS type-stripping)
//      or: bun test ts/confirm.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { JSDOM } from "jsdom";
import { resolveConfirmMessage } from "./confirm.ts";

function formFrom(html: string): HTMLFormElement {
	const dom = new JSDOM(`<body>${html}</body>`);
	const form = dom.window.document.querySelector("form");
	assert.ok(form, "fixture must contain a <form>");
	if (!form) throw new Error("unreachable"); // keeps TS's null-narrowing happy without `!`
	return form as unknown as HTMLFormElement;
}

test("resolveConfirmMessage: a static data-confirm is returned verbatim", () => {
	const form = formFrom(`<form data-confirm="Delete this?"></form>`);
	assert.equal(resolveConfirmMessage(form), "Delete this?");
});

test("resolveConfirmMessage: no data-confirm attribute at all yields no message (submits silently)", () => {
	const form = formFrom("<form></form>");
	assert.equal(resolveConfirmMessage(form), undefined);
});

test("resolveConfirmMessage: data-confirm-template fills {preset} from the named field's CURRENT value", () => {
	const form = formFrom(`
		<form data-confirm-template="Replace classic with the '{preset}' preset?" data-confirm-field="preset">
			<select name="preset"><option value="quartet" selected>quartet</option><option value="classic">classic</option></select>
		</form>`);
	const select = form.querySelector("select") as HTMLSelectElement;

	// The user changes the select AFTER the page rendered — the resolved message must reflect
	// THIS value, the exact bug class methodology-ui-footgun-after-cho-2c69c7 reports (a stale
	// baked-in value would misname what the destructive action actually does).
	select.value = "classic";
	assert.equal(resolveConfirmMessage(form), "Replace classic with the 'classic' preset?");
});

test("resolveConfirmMessage: template tracks the field's rendered default (before any hand-change) too", () => {
	const form = formFrom(`
		<form data-confirm-template="Load preset '{preset}'?" data-confirm-field="preset">
			<select name="preset"><option value="quartet" selected>quartet</option><option value="classic">classic</option></select>
		</form>`);
	assert.equal(resolveConfirmMessage(form), "Load preset 'quartet'?");
});

test("resolveConfirmMessage: a missing field resolves to an empty {preset}, not a crash", () => {
	const form = formFrom(`<form data-confirm-template="Load '{preset}'?" data-confirm-field="preset"></form>`);
	assert.equal(resolveConfirmMessage(form), "Load ''?");
});

test("resolveConfirmMessage: {preset} appears twice — both occurrences are filled (replaceAll, not replace)", () => {
	const form = formFrom(`
		<form data-confirm-template="'{preset}' again: '{preset}'?" data-confirm-field="preset">
			<select name="preset"><option value="classic" selected>classic</option></select>
		</form>`);
	assert.equal(resolveConfirmMessage(form), "'classic' again: 'classic'?");
});
