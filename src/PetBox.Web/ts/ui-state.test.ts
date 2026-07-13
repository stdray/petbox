// Unit tests for the pure cookie parse/merge functions behind the ui-state-framework mechanism.
// document.cookie reading/writing itself needs a real DOM (a Playwright/E2E concern, exercised by
// whichever follow-up first wires a real [BrowserState] property end-to-end); the parse/merge
// logic factors cleanly into plain functions and is covered here without a browser, mirroring
// board.test.ts's split for parseSortPref/parseViewPref.
// Run: node --test ts/ui-state.test.ts   (Node >= 23.6 native TS type-stripping; no build step)
//      or: bun test ts/ui-state.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { mergeUiStateCookie, parseUiStateCookie } from "./ui-state.ts";

test("parseUiStateCookie: missing cookie reads as {}", () => {
	assert.deepEqual(parseUiStateCookie(null), {});
});

test("parseUiStateCookie: malformed JSON reads as {}, never throws", () => {
	assert.deepEqual(parseUiStateCookie("not json"), {});
	assert.deepEqual(parseUiStateCookie("{unterminated"), {});
});

test("parseUiStateCookie: a JSON value that isn't a plain object reads as {}", () => {
	assert.deepEqual(parseUiStateCookie("42"), {});
	assert.deepEqual(parseUiStateCookie('"a string"'), {});
	assert.deepEqual(parseUiStateCookie("[1,2,3]"), {});
	assert.deepEqual(parseUiStateCookie("null"), {});
});

test("parseUiStateCookie: round-trips a plain object", () => {
	assert.deepEqual(parseUiStateCookie(JSON.stringify({ a: 1, b: "x", c: true })), { a: 1, b: "x", c: true });
});

test("mergeUiStateCookie: patch overlays onto an existing cookie written by another feature", () => {
	const existing = JSON.stringify({ sidebarPinned: true });
	const merged = mergeUiStateCookie(existing, { kqlPanelPinned: false });
	assert.deepEqual(JSON.parse(merged), { sidebarPinned: true, kqlPanelPinned: false });
});

test("mergeUiStateCookie: patch overwrites only its own key, siblings survive", () => {
	const existing = JSON.stringify({ sidebarPinned: true, kqlPanelPinned: true });
	const merged = mergeUiStateCookie(existing, { kqlPanelPinned: false });
	assert.deepEqual(JSON.parse(merged), { sidebarPinned: true, kqlPanelPinned: false });
});

test("mergeUiStateCookie: no existing cookie starts from {}", () => {
	const merged = mergeUiStateCookie(null, { a: 1 });
	assert.deepEqual(JSON.parse(merged), { a: 1 });
});

test("mergeUiStateCookie: malformed existing cookie is treated as empty, not fatal", () => {
	const merged = mergeUiStateCookie("garbage{{{", { a: 1 });
	assert.deepEqual(JSON.parse(merged), { a: 1 });
});
