// Regression test for reader-view-body-and-comments: Firefox reader mode (Readability.js)
// must surface BOTH the task body AND the comment thread on a node-detail page — not just
// whichever one Readability's density/keyword heuristic picks as the sole winner.
//
// Root cause: Readability scores candidate nodes by text density + a class/id keyword weight,
// then keeps ONE winning subtree and appends only ITS siblings. The node body (`node-read-body`)
// and the comment thread (`node-comments`) sat in separate subtrees, both reusing the `md-body`
// class (a positive Readability keyword), so whichever had more text won outright and the other
// was dropped.
//
// Fix, in two rounds (both SSR markup / TS selector changes — no stored-data change, no
// interactive-behavior change):
//
// Round 1 — wrap node-read-body and the comment thread under one shared
// `<article class="node-reader-content">`, plus an `rv-content` class (matches Readability's
// "content" positive keyword; never "comment" — that one is a NEGATIVE keyword, kept out of
// every class/id here on purpose) so the shared root scores well enough to be a candidate.
// This alone fixed the short-body + one-moderate-comment shape (node-detail-reader-view.html)
// but NOT the real ideas/memory-store-creation-guard node (node-detail-reader-view-dense.html,
// short body + 3 real comments, one a long link-dense technical analysis) — verified via
// `@mozilla/readability --debug` candidate scores, the body kept getting dropped.
//
// Round 2 — the remaining problem was ancestor DEPTH: comment text sat 4 levels below the
// shared article (md-body → comment-read-body → "comment" div → node-comments wrapper →
// article), and Readability's per-ancestor score divides by roughly (level*3) past the second
// ancestor, so by the time a paragraph's score reached the shared root it was diluted far enough
// that Readability's climb-then-stop promotion locked onto the inner "comment" div and never
// reached the root. Fix: deleted the `comment-read-body` wrapper (the md-body div itself is now
// directly toggled — ts/commentThread.ts updated to query `comment-body` instead) and deleted
// _CommentThread.cshtml's own outer wrapper (`node-comments` moved onto the shared `<article>`
// in TaskBoardNode.cshtml) — cutting comment text's distance to the shared root from 4 levels to
// 2. Interactive behavior (reply/edit/delete toggles, hover-reveal, threading indent) is
// unchanged: every element ts/commentThread.ts depends on (`comment`, `comment-edit-form`,
// `comment-reply-form`, `comment-body`) still exists with the same `.closest()`/`querySelector()`
// relationships, just one wrapper div shallower — see the task report for the full
// selector-coupling walkthrough.
//
// Fixtures are REAL rendered node-detail pages — captured via the test host
// (tests/PetBox.Tests/Web/ScratchReaderViewCapture.cs, run to regenerate, then removed) rather
// than hand-built. node-detail-reader-view.html: a short body + one moderate synthetic comment.
// node-detail-reader-view-dense.html: the ACTUAL ideas/memory-store-creation-guard node, body +
// all 3 real comments pulled verbatim via tasks_node_get/comments_search — the exact case
// originally reported as broken.
//
// Verified empirically: on the pre-round-2 markup, the dense fixture extracted all 3 comments
// but dropped the body (textContent present, body marker absent). After round 2, both fixtures
// extract body + every comment. See the task report for the full before/after transcript.
//
// Run: node --test ts/readerView.test.ts   (Node >= 23.6 native TS type-stripping; no build step)
//      or: bun test ts/readerView.test.ts

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { Readability, isProbablyReaderable } from "@mozilla/readability";
import { JSDOM } from "jsdom";

function fixturePath(name: string): string {
	return fileURLToPath(new URL(`./testdata/${name}`, import.meta.url));
}

function extractReaderView(html: string) {
	const dom = new JSDOM(html, { url: "https://petbox.example/ui/ws/proj/tasks/board/node" });
	const readerable = isProbablyReaderable(dom.window.document);
	const article = new Readability(dom.window.document).parse();
	return { readerable, article };
}

test("reader view: short body + one moderate comment both survive extraction", () => {
	const html = readFileSync(fixturePath("node-detail-reader-view.html"), "utf8");
	const { readerable, article } = extractReaderView(html);

	assert.ok(readerable, "fixture page should be flagged reader-mode-eligible");
	assert.ok(article, "Readability.parse() should return an article, not null");
	if (!article) return; // unreachable after assert.ok, keeps TS's null-narrowing happy without `!`

	const text = article.textContent;
	assert.match(text, /BODY_MARKER/, "node body text must survive extraction");
	assert.match(text, /COMMENT_MARKER/, "comment text must survive extraction");
});

test("reader view: the real ideas/memory-store-creation-guard node (body + all 3 comments) survives extraction", () => {
	const html = readFileSync(fixturePath("node-detail-reader-view-dense.html"), "utf8");
	const { readerable, article } = extractReaderView(html);

	assert.ok(readerable, "fixture page should be flagged reader-mode-eligible");
	assert.ok(article, "Readability.parse() should return an article, not null");
	if (!article) return;

	const text = article.textContent;
	// The node body's last line (distinctive, appears nowhere else in the fixture).
	assert.match(text, /Ждёт выбора владельца/, "node body text must survive extraction");
	// One distinctive phrase per real comment.
	assert.match(text, /Scenario-first переоценка/, "comment 1 (analysis) must survive extraction");
	assert.match(text, /жёсткий opt-in\), решение владельца/, "comment 2 (spec_plan) must survive extraction");
	assert.match(text, /дополнение владельца/, "comment 3 (spec_plan addendum) must survive extraction");
});
