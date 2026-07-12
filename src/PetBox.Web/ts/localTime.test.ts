// Unit tests for formatLocalTime's ms opt-in (logs-keep-millis).
//
// Regression pinned here: a prior fix (petbox-date-format-ms-noise) dropped milliseconds
// unconditionally inside the one shared formatLocalTime() that backs every
// `<time class="local-time">` in the UI, which silently also stripped ms from log rows and
// trace rows — where sub-second precision is data (event ordering within a second, span
// duration), not noise. The fix makes ms precision a per-call/per-element parameter: callers
// that render log/trace timestamps pass withMs=true (and mark their `<time>` with `data-ms`,
// which renderLocalTimes reads); board/comment/dashboard/share callers keep the default
// (withMs=false). This test locks the boolean behavior so a future "simplify the signature"
// pass can't quietly re-collapse it back to always-false.
//
// Run: node --test ts/localTime.test.ts   (Node >= 23.6 native TS type-stripping; no build step)
//      or: bun test ts/localTime.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { formatLocalTime } from "./localTime.ts";

const SAMPLE_ISO = "2026-07-12T10:23:45.678Z";

test("formatLocalTime: withMs=false (board/comment/dashboard/share default) omits milliseconds", () => {
	const out = formatLocalTime(SAMPLE_ISO, false);
	assert.match(out, /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/);
});

test("formatLocalTime: withMs=true (logs/traces opt-in) keeps a zero-padded 3-digit millisecond suffix", () => {
	const out = formatLocalTime(SAMPLE_ISO, true);
	assert.match(out, /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$/);
});

test("formatLocalTime: the withMs flag is the only difference between the two renderings", () => {
	const withoutMs = formatLocalTime(SAMPLE_ISO, false);
	const withMs = formatLocalTime(SAMPLE_ISO, true);
	assert.equal(withMs.startsWith(withoutMs), true);
	assert.equal(withMs.length, withoutMs.length + 4); // ".678"
});

test("formatLocalTime: an unparsable datetime is returned unchanged regardless of withMs", () => {
	assert.equal(formatLocalTime("not-a-date", false), "not-a-date");
	assert.equal(formatLocalTime("not-a-date", true), "not-a-date");
});
