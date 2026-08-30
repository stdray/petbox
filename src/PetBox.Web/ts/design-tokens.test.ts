// The body design layer's CSS contract (work `node-render-design-layer`).
//
// There are no visual-regression tests in this repo and this file does not pretend to be one — it
// cannot tell you the page looks right. What it CAN prove is the two things that break silently
// and are invisible in a screenshot review of one theme:
//
//   1. THE DARK GUARD. Dark values are declared twice — once under `prefers-color-scheme`, once
//      under an explicit `[data-theme="dark"]` — so the theme switcher wins in both directions.
//      The trap is asymmetry: a guard that excludes only `light` also matches `nord` and `retro`,
//      which are LIGHT daisyUI themes in this app (tailwind.config.js ships four themes). A nord
//      user on an OS-dark machine would then get dark ink on a light backdrop. The guard is
//      therefore asserted by actually MATCHING it against a real root element per theme, not by
//      grepping for the selector text.
//
//   2. NO WIDTH CAP. `node-detail-read-width` deliberately removed the global 80ch cap from the
//      container. `node-render-design-layer` briefly reintroduced a 66ch measure for paragraphs
//      only, and the owner then asked for it back out entirely. A cap that creeps back onto
//      .md-body or .md-body p would be a silent revert of either decision.
//
// Run: bun test ts/design-tokens.test.ts

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";

// Comments are stripped up front: this file is heavily commented, and a comment sitting above a
// rule otherwise lands inside the "selector" of the next match.
const css = readFileSync(fileURLToPath(new URL("./app.css", import.meta.url)), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");

// Return the balanced `{...}` body that starts at or after `from`. A regex cannot do this: the
// `@media` wrapper nests one level.
function balancedBody(source: string, from: number): string {
	const open = source.indexOf("{", from);
	assert.notEqual(open, -1, `no block opens after index ${from}`);
	let depth = 0;
	for (let i = open; i < source.length; i++) {
		if (source[i] === "{") depth++;
		else if (source[i] === "}" && --depth === 0) return source.slice(open + 1, i);
	}
	throw new Error("unbalanced braces in app.css");
}

// The body of the first rule whose selector text matches exactly.
function ruleBody(selector: string): string {
	const at = css.indexOf(`\n${selector} {`);
	assert.notEqual(at, -1, `expected a \`${selector}\` rule in app.css`);
	return balancedBody(css, at);
}

function declaredTokens(body: string): string[] {
	return [...body.matchAll(/--(md-[a-z0-9-]+)\s*:/g)].map((m) => m[1]).sort();
}

const NEUTRAL = [
	"md-ground",
	"md-surface",
	"md-surface-2",
	"md-ink",
	"md-ink-soft",
	"md-muted",
	"md-rule",
	"md-rule-strong",
];
const SEMANTIC = [
	"md-live",
	"md-live-fill",
	"md-broken",
	"md-broken-fill",
	"md-partial",
	"md-partial-fill",
	"md-proposed",
	"md-proposed-fill",
];

test("token palette: 3 backdrops, 3 text weights, 2 rule weights, 4 semantic outline+fill pairs", () => {
	const root = declaredTokens(ruleBody(":root"));
	for (const token of [...NEUTRAL, ...SEMANTIC]) assert.ok(root.includes(token), `:root must define --${token}`);
	// Each semantic colour is a PAIR — an outline token and a fill token, never a lone colour.
	for (const name of ["live", "broken", "partial", "proposed"]) {
		assert.ok(root.includes(`md-${name}`), `--md-${name} (outline) missing`);
		assert.ok(root.includes(`md-${name}-fill`), `--md-${name}-fill missing`);
	}
});

// The two dark declarations, located once and reused by the tests below.
function darkBlocks() {
	const mediaAt = css.indexOf("@media (prefers-color-scheme: dark)");
	assert.notEqual(mediaAt, -1, "the prefers-color-scheme dark block is missing entirely");
	const mediaBody = balancedBody(css, mediaAt);
	const guard = mediaBody.slice(0, mediaBody.indexOf("{")).trim();
	const media = balancedBody(mediaBody, 0);
	const explicit = ruleBody(':root[data-theme="dark"]');
	return { guard, media, explicit };
}

test("dark tokens are declared twice, and the two declarations agree", () => {
	const { media, explicit } = darkBlocks();
	const inMedia = declaredTokens(media);
	const inExplicit = declaredTokens(explicit);

	assert.ok(inMedia.length > 0, "the prefers-color-scheme block declares no tokens");
	assert.deepEqual(
		inMedia,
		inExplicit,
		"the OS-preference and the explicit [data-theme=dark] blocks must override the SAME tokens — " +
			"a token overridden in only one of them changes meaning depending on how dark was reached",
	);

	// A dark block may only RE-declare tokens; a token that exists nowhere else has no light value.
	const root = declaredTokens(ruleBody(":root"));
	for (const token of inExplicit)
		assert.ok(root.includes(token), `--${token} is overridden for dark but never defined in :root`);
});

test("dark blocks override ONLY tokens — no component rule hides in a theme block", () => {
	const { media, explicit } = darkBlocks();
	for (const [name, body] of [
		["prefers-color-scheme", media],
		["[data-theme=dark]", explicit],
	] as const) {
		assert.equal(body.includes("{"), false, `${name} block must contain no nested rule`);
		for (const decl of body
			.split(";")
			.map((d) => d.trim())
			.filter(Boolean))
			assert.ok(
				decl.startsWith("--md-"),
				`${name} block declares a non-token property (${decl}) — dark must move tokens, not styling`,
			);
	}
});

test("the dark guard matches dark and every no-theme root, and NO light theme", () => {
	const { guard } = darkBlocks();
	const dom = new JSDOM("<!doctype html><html><body></body></html>");
	const root = dom.window.document.documentElement;

	const matchesWith = (theme: string | null): boolean => {
		if (theme === null) root.removeAttribute("data-theme");
		else root.setAttribute("data-theme", theme);
		return root.matches(guard);
	};

	// Reached by OS preference with nothing pinned, or pinned explicitly to dark.
	assert.ok(matchesWith(null), "a root with no data-theme must take the OS dark tokens");
	assert.ok(matchesWith("dark"), "an explicitly dark root must take the dark tokens");

	// Every LIGHT theme this app ships must be excluded — not just `light`. daisyUI's nord and
	// retro are light themes; letting the OS preference push dark tokens onto them puts dark ink
	// on a light backdrop.
	for (const light of ["light", "nord", "retro"])
		assert.equal(
			matchesWith(light),
			false,
			`[data-theme="${light}"] is a LIGHT theme and must not pick up dark tokens from the OS preference`,
		);
});

test("the explicit dark rule is scoped to the dark theme alone", () => {
	const dom = new JSDOM("<!doctype html><html><body></body></html>");
	const root = dom.window.document.documentElement;
	root.setAttribute("data-theme", "light");
	assert.equal(root.matches(':root[data-theme="dark"]'), false);
	root.setAttribute("data-theme", "dark");
	assert.ok(root.matches(':root[data-theme="dark"]'));
});

test("no reading measure caps .md-body or .md-body p — the container cap node-detail-read-width removed must stay removed", () => {
	const rules = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map(([, sel, body]) => ({
		selector: sel.trim().replace(/\s+/g, " "),
		body,
	}));

	const measured = rules.filter((r) => /max-width\s*:/.test(r.body) && /^\.md-body(\s+p)?$/.test(r.selector));
	assert.equal(
		measured.length,
		0,
		"no max-width measure may sit on .md-body or .md-body p — the reading-width cap was dropped " +
			"and the global container cap node-detail-read-width removed must not come back either",
	);
});

// ── The long-code-block fold (work `md-code-block-height-cap`) ──────────────────────────────────
//
// The feature is split across three files that must agree, and every way it breaks is silent:
//
//   * MarkdownDesignLayer.cs decides WHICH blocks fold (source lines > FoldLineThreshold);
//   * this sheet caps the folded ones at what is supposed to be that same number of lines;
//   * the section container's "a code block may reach my edges" rule is a DIRECT-child selector
//     that the new wrapper silently steps out from under.
//
// So the cap is not asserted as a pixel value — it is asserted to be DERIVED from the things it
// depends on. `max-height: calc(10lh + 1.2em)` is only correct while 10 is the renderer's threshold
// and 1.2em is twice the block's vertical padding.
//
// The `lh` unit is itself load-bearing and the first version of this feature got it wrong, so the
// mistake is pinned out below rather than just fixed: the cap was written as ten times the CODE's
// line box (0.85em × 1.62), which is smaller than the PRE's strut — and a line box is never shorter
// than its block's strut, so the box rendered eight and a half lines while every factor in it
// looked right. `1lh` is the pre's own line-height, which IS the pitch the lines lay out at.
const foldCapRx = /max-height:\s*calc\(\s*(\d+)lh\s*\+\s*([\d.]+)em\s*\)/;

// The single declaration's value for `prop` in a rule body, e.g. "0.85em".
function decl(body: string, prop: string): string {
	const m = new RegExp(`(?:^|;)\\s*${prop}\\s*:\\s*([^;]+)`).exec(body);
	assert.ok(m, `expected a \`${prop}\` declaration in \`${body.trim()}\``);
	return m[1].trim();
}

test("the code-fold cap is DERIVED from the renderer's threshold and the block's own padding", () => {
	const capped = foldCapRx.exec(ruleBody(".md-body .md-code-fold > pre"));
	assert.ok(capped, "`.md-body .md-code-fold > pre` must cap the block with a calc() in the documented shape");
	const [, lines, padding] = capped;

	// The line count is the RENDERER's threshold — read from the C# source, not restated here. A
	// cap of 10 lines under a threshold of 25 would fold nothing visible for fifteen more lines.
	const layer = readFileSync(fileURLToPath(new URL("../Rendering/MarkdownDesignLayer.cs", import.meta.url)), "utf8");
	const threshold = /FoldLineThreshold\s*=\s*(\d+)/.exec(layer);
	assert.ok(threshold, "MarkdownDesignLayer.cs must declare FoldLineThreshold");
	assert.equal(lines, threshold[1], "the CSS cap must show exactly as many lines as the renderer calls 'long'");

	// Plus the block's own vertical padding, top and bottom — otherwise the last line is eaten by
	// the padding the cap forgot to account for.
	const vertical = Number.parseFloat(decl(ruleBody(".md-body pre"), "padding").split(/\s+/)[0]);
	assert.equal(Number.parseFloat(padding), vertical * 2, "the cap must add the pre's top AND bottom padding");
});

test("the cap measures the PRE's line box, never the code's smaller one", () => {
	// The bug this pins out: `.md-body pre code` renders at 0.85em, but a line box is never shorter
	// than its block's strut, so the pre's line-height is the pitch the lines actually lay out at.
	// A cap expressed through the code's font-size measures something the page never renders.
	const cap = ruleBody(".md-body .md-code-fold > pre");
	const codeScale = decl(ruleBody(".md-body pre code"), "font-size");
	assert.equal(
		cap.includes(Number.parseFloat(codeScale).toString()),
		false,
		`the cap must not be built from the code font-size (${codeScale}) — it is not what the lines are spaced by`,
	);
	assert.match(cap, /\dlh/, "the cap is expressed in `lh`, the pre's own line-height");
});

test("a short code block is never capped — only the wrapper the renderer adds is", () => {
	// The whole point of deciding server-side. A `max-height` on the bare `.md-body pre` would cap
	// every two-line snippet as well, and no test of the renderer could see it.
	assert.equal(/max-height/.test(ruleBody(".md-body pre")), false, "`.md-body pre` must carry no height cap");
	assert.equal(
		/overflow-x/.test(ruleBody(".md-body .md-code-fold > pre")),
		false,
		"the fold must not restate overflow-x — the block keeps the horizontal behaviour it already had",
	);
});

// Work `md-code-wrap-not-scroll`. The owner's decision: a long command wraps, it is never parked
// behind a horizontal scrollbar. Both halves are load-bearing and this pins both, because dropping
// `overflow-wrap` is the silent half-fix — `pre-wrap` on its own looks like it works until the
// content is a 298-character line with no space in it, which is exactly the content that prompted
// the work.
test("a code block WRAPS long lines instead of hiding them behind a horizontal scrollbar", () => {
	const pre = ruleBody(".md-body pre");
	assert.match(pre, /white-space:\s*pre-wrap/, "long lines must wrap, not extend past the block's right edge");
	assert.match(
		pre,
		/overflow-wrap:\s*anywhere/,
		"`pre-wrap` alone cannot break a token with no space in it — a long URL or command would still overflow",
	);
});

// The same sentence in reverse, and the boundary the work was told not to cross: a TABLE still
// scrolls. A table cannot reflow into a narrow column without destroying the row alignment that
// makes it readable, so `.md-table-scroll` keeps its own scroller and must never pick up the
// wrapping rules above.
test("wrapping is code-block-only — a table still scrolls horizontally", () => {
	const scroll = ruleBody(".md-body .md-table-scroll");
	assert.match(scroll, /overflow-x:\s*auto/, "a wide table still scrolls inside its own wrapper");
	assert.equal(
		/white-space|overflow-wrap/.test(scroll),
		false,
		"the code-block wrapping decision must not leak onto tables",
	);
});

test("the cap is lifted by the native <details>, with no script in the loop", () => {
	// `.md-body` renders on Pages/ShareNode.cshtml, an anonymous page whose layout ships this
	// stylesheet and no JS bundle at all: a script-driven fold would be stuck shut there forever.
	const open = ruleBody(".md-body .md-code-fold:has(> .md-code-fold-toggle[open]) > pre");
	assert.match(open, /max-height:\s*none/, "opening the disclosure must remove the cap outright");
});

test("without :has() support BOTH the cap and the control are dropped, never the cap alone", () => {
	// The failure mode being excluded: a clipped block next to a control that cannot lift it.
	const at = css.indexOf("@supports not selector(:has(*))");
	assert.notEqual(at, -1, "a `@supports not selector(:has(*))` fallback must exist");
	const body = balancedBody(css, at);
	assert.match(body, /\.md-body \.md-code-fold > pre \{[^}]*max-height:\s*none/, "the cap must be lifted");
	assert.match(body, /\.md-body \.md-code-fold-toggle \{[^}]*display:\s*none/, "the dead control must be hidden");
});

test("a folded block still reaches the edges of its section", () => {
	// `.md-section > pre` stops matching the moment the fold wrapper sits between the two, and the
	// block would quietly lose the surface that distinguishes it inside a section.
	const rules = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map(([, sel, body]) => ({
		selector: sel.trim().replace(/\s+/g, " "),
		body,
	}));
	const edge = rules.find((r) => r.selector.includes(".md-body .md-section > pre"));
	assert.ok(edge, "the section's code-block surface rule is missing entirely");
	assert.ok(
		edge.selector.includes(".md-body .md-section > .md-code-fold > pre"),
		"the section surface rule must also name the FOLDED shape of the same block",
	);
});
