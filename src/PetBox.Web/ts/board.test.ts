// Unit tests for the pure sort-comparison functions behind the board's client-side sort toggle
// (board-sort-impl). The DOM reorder itself (reorderDom, inside initBoardPage) needs a real
// document and is out of reach here — that's a Playwright concern — but the key-extraction and
// comparator logic factors cleanly into plain functions, so it's covered without a browser.
// Run: node --test ts/board.test.ts   (Node >= 23.6 native TS type-stripping; no build step)
//      or: bun test ts/board.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { compareSortValues, parseSortPref, sortKeyValue } from "./board.ts";

test("parseSortPref: defaults to priority/asc for null, malformed, or unknown-key input", () => {
	assert.deepEqual(parseSortPref(null), { by: "priority", desc: false });
	assert.deepEqual(parseSortPref("not json"), { by: "priority", desc: false });
	assert.deepEqual(parseSortPref(JSON.stringify({ by: "bogus", desc: true })), { by: "priority", desc: false });
});

test("parseSortPref: round-trips a valid stored preference", () => {
	assert.deepEqual(parseSortPref(JSON.stringify({ by: "title", desc: true })), { by: "title", desc: true });
	assert.deepEqual(parseSortPref(JSON.stringify({ by: "created" })), { by: "created", desc: false });
});

test("sortKeyValue: priority reads as a number, missing defaults to 0", () => {
	assert.equal(sortKeyValue({ priority: "50" }, "priority"), 50);
	assert.equal(sortKeyValue({}, "priority"), 0);
});

test("sortKeyValue: title falls back to the node key when blank, lowercased either way", () => {
	assert.equal(sortKeyValue({ title: "Alpha" }, "title"), "alpha");
	assert.equal(sortKeyValue({ title: "", nodeKey: "Zeta/Leaf" }, "title"), "zeta/leaf");
});

test("sortKeyValue: created/updated parse ISO timestamps; missing/unparsable reads as 0", () => {
	const t = sortKeyValue({ created: "2026-01-01T00:00:00.000Z" }, "created");
	assert.equal(typeof t, "number");
	assert.ok((t as number) > 0);
	assert.equal(sortKeyValue({}, "created"), 0);
	assert.equal(sortKeyValue({ updated: "not-a-date" }, "updated"), 0);
});

test("compareSortValues: numeric and string comparisons order ascending (desc is the caller's job)", () => {
	assert.ok(compareSortValues(1, 2) < 0);
	assert.ok(compareSortValues(2, 1) > 0);
	assert.equal(compareSortValues(1, 1), 0);
	assert.ok(compareSortValues("a", "b") < 0);
	assert.ok(compareSortValues("b", "a") > 0);
});
