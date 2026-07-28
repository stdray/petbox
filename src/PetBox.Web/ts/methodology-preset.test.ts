// The preset lead swap (ui-preset-lead-vs-select): the admin tasks page renders one lead paragraph
// per provisioning preset and the module shows exactly the one the select names — the whole point
// being that the text above the Enable button can never describe a preset other than the selected
// one. Driven over a jsdom fragment shaped like the rendered markup (same jsdom the reader-view
// test uses); the server side of the same invariant is asserted in ModuleViewsTests.
//
// Run: node --test ts/methodology-preset.test.ts   (Node >= 23.6 native TS type-stripping)
//      or: bun test ts/methodology-preset.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { JSDOM } from "jsdom";
import { applyPresetLead } from "./methodology-preset.ts";

function fragment(): { leads: HTMLElement[]; visible: () => string[] } {
	const dom = new JSDOM(`
		<div data-testid="methodology-preset-lead">
			<p data-preset-lead="quartet">four linked boards</p>
			<p data-preset-lead="classic" hidden>one board</p>
		</div>`);
	const leads = [...dom.window.document.querySelectorAll("[data-preset-lead]")] as unknown as HTMLElement[];
	return {
		leads,
		visible: () => leads.filter((l) => !l.hidden).map((l) => l.dataset["presetLead"] ?? ""),
	};
}

test("applyPresetLead: the server-rendered default already shows exactly one lead", () => {
	const { visible } = fragment();
	assert.deepEqual(visible(), ["quartet"]);
});

test("applyPresetLead: selecting classic hides the quartet lead and shows classic's", () => {
	const { leads, visible } = fragment();
	applyPresetLead(leads, "classic");
	assert.deepEqual(visible(), ["classic"]);
});

test("applyPresetLead: switching back is symmetric — never two leads, never zero", () => {
	const { leads, visible } = fragment();
	applyPresetLead(leads, "classic");
	applyPresetLead(leads, "quartet");
	assert.deepEqual(visible(), ["quartet"]);
});

test("applyPresetLead: an unknown slug hides everything rather than showing a wrong promise", () => {
	const { leads, visible } = fragment();
	applyPresetLead(leads, "no-such-preset");
	assert.deepEqual(visible(), []);
});
