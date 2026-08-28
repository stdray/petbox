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
