// Regression test for reader-view-body-and-comments: Firefox reader mode (Readability.js)
// must surface BOTH the task body AND the comment thread on a node-detail page — not just
// whichever one Readability's density/keyword heuristic picks as the sole winner.
//
// Root cause (fixed in TaskBoardNode.cshtml / _CommentThread.cshtml, SSR markup only):
// Readability scores candidate nodes by text density + a class/id keyword weight, then keeps
// ONE winning subtree and appends only ITS siblings. Before the fix, the node body (inside
// `node-read-body`) and the comment thread (`node-comments`) sat in separate subtrees under
// `card-body`; both reused the same `md-body` class (a positive Readability keyword), so
// whichever had more text won outright and the other was dropped. The fix wraps both under one
// shared `<article class="node-reader-content">` content root and adds a `rv-content` class
// (matches Readability's "content" positive keyword, NOT "comment" — that one is negative) to
// nudge Readability's candidate climb through to that shared root instead of stopping one level
// too early inside a single comment's own wrapper.
//
// The fixture (ts/testdata/node-detail-reader-view.html) is the REAL rendered node-detail page —
// captured via the test host (tests/PetBox.Tests/Web/ScratchReaderViewCapture.cs, run once, then
// removed) for a short body + one substantial comment, i.e. exactly the failing shape reported
// against ideas/memory-store-creation-guard. This is not a hand-built approximation: it is the
// actual Razor output (same wrappers, same `md-body`/`data-testid` markup) the browser receives.
//
// Verified empirically before landing this fix (see the task's PR/commit for the full
// transcript): on the ORIGINAL (pre-fix) markup, this exact scenario extracts the comment only
// (textContent ~594 chars, BODY_MARKER absent). On the fixed markup below, both survive
// (~844 chars, both markers present). A known residual limit: when ONE comment's own paragraph
// density heavily dominates a rich, link-dense multi-comment thread (e.g. the real
// ideas/memory-store-creation-guard node, which has three long comments full of `file.cs:line`
// references), Readability's climb-then-stop algorithm can still settle one level below the
// shared root and drop the body — this fix narrows that gap but does not close it for every
// input shape; see the task report for the full analysis.
//
// Run: node --test ts/readerView.test.ts   (Node >= 23.6 native TS type-stripping; no build step)
//      or: bun test ts/readerView.test.ts

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { Readability, isProbablyReaderable } from "@mozilla/readability";
import { JSDOM } from "jsdom";

const FIXTURE_PATH = fileURLToPath(new URL("./testdata/node-detail-reader-view.html", import.meta.url));
const BODY_MARKER = "BODY_MARKER";
const COMMENT_MARKER = "COMMENT_MARKER";

function extractReaderView(html: string) {
	const dom = new JSDOM(html, { url: "https://petbox.example/ui/ws/proj/tasks/board/node" });
	const readerable = isProbablyReaderable(dom.window.document);
	const article = new Readability(dom.window.document).parse();
	return { readerable, article };
}

test("reader view: node body and comment thread both survive Readability extraction", () => {
	const html = readFileSync(FIXTURE_PATH, "utf8");
	const { readerable, article } = extractReaderView(html);

	assert.ok(readerable, "fixture page should be flagged reader-mode-eligible");
	assert.ok(article, "Readability.parse() should return an article, not null");
	if (!article) return; // unreachable after assert.ok, keeps TS's null-narrowing happy without `!`

	const text = article.textContent;
	assert.match(text, new RegExp(BODY_MARKER), "node body text must survive extraction");
	assert.match(text, new RegExp(COMMENT_MARKER), "comment text must survive extraction");
});
