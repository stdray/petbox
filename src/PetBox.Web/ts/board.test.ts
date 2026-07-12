// Unit tests for the pure sort-comparison functions behind the board's client-side sort toggle
// (board-sort-impl). The DOM reorder itself (reorderDom, inside initBoardPage) needs a real
// document and is out of reach here — that's a Playwright concern — but the key-extraction and
// comparator logic factors cleanly into plain functions, so it's covered without a browser.
// Run: node --test ts/board.test.ts   (Node >= 23.6 native TS type-stripping; no build step)
//      or: bun test ts/board.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { compareSortValues, parseSortPref, parseViewPref, sortKeyValue, viewPrefNeedsReconcile } from "./board.ts";

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

// board-view-persistence: parseViewPref backs initBoardViewPersistence's reconcile-on-load
// redirect and its click-to-save handler — pure JSON-shape parsing, no DOM needed.
test("parseViewPref: null/malformed/shapeless input reads as absent", () => {
	assert.equal(parseViewPref(null), null);
	assert.equal(parseViewPref("not json"), null);
	assert.equal(parseViewPref("{}"), null);
	assert.equal(parseViewPref(JSON.stringify({ mode: "" })), null);
	assert.equal(parseViewPref(JSON.stringify({ mode: 42 })), null);
});

test("parseViewPref: round-trips a mode-only preference", () => {
	assert.deepEqual(parseViewPref(JSON.stringify({ mode: "tree" })), { mode: "tree" });
});

test("parseViewPref: round-trips a mode+by preference (tags)", () => {
	assert.deepEqual(parseViewPref(JSON.stringify({ mode: "tags", by: "area,concern" })), { mode: "tags", by: "area,concern" });
});

test("parseViewPref: a non-string/blank `by` is dropped, not carried through as garbage", () => {
	assert.deepEqual(parseViewPref(JSON.stringify({ mode: "tags", by: "" })), { mode: "tags" });
	assert.deepEqual(parseViewPref(JSON.stringify({ mode: "tags", by: 7 })), { mode: "tags" });
});

// board-view-persistence regression: initBoardViewPersistence used to compare ONLY `mode`
// against data-resolved-view, so a saved {mode:"tags", by:"area"} silently lost its `by`
// whenever the server's own by-less resolution already reported "tags" (e.g. a methodology
// defaultView of "tags", with no `by` on the URL) — mode matched, no redirect fired, and the
// page rendered the by-less tags degradation instead of the saved grouping.
test("viewPrefNeedsReconcile: no saved pref never reconciles", () => {
	assert.equal(viewPrefNeedsReconcile(null, "tree", undefined), false);
});

test("viewPrefNeedsReconcile: mode differs -> reconcile", () => {
	assert.equal(viewPrefNeedsReconcile({ mode: "tags", by: "area" }, "tree", undefined), true);
});

test("viewPrefNeedsReconcile: same mode, `by` differs -> reconcile (the defect this test pins)", () => {
	assert.equal(viewPrefNeedsReconcile({ mode: "tags", by: "area" }, "tags", ""), true);
	assert.equal(viewPrefNeedsReconcile({ mode: "tags", by: "area" }, "tags", "concern"), true);
});

test("viewPrefNeedsReconcile: same mode and by -> no reconcile", () => {
	assert.equal(viewPrefNeedsReconcile({ mode: "tags", by: "area" }, "tags", "area"), false);
	assert.equal(viewPrefNeedsReconcile({ mode: "tree" }, "tree", undefined), false);
	assert.equal(viewPrefNeedsReconcile({ mode: "tree" }, "tree", ""), false);
});
