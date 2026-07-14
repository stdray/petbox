// board-search-stem-lookup's acceptance gate, TS side. Reads the SAME fixture
// BoardSearchStemFixtureTests.cs (tests/PetBox.Tests/Core/Search) asserts against — the one file
// both a C# test and a TS test read, so a stemmer/tokenizer divergence between the server
// (PetBox.Core.Search.TokenStemmer/FtsQuery, via BoardSearchIndexBuilder) and the client (this
// module, ts/search-index.ts) fails the gate on BOTH sides instead of silently shipping a search
// that misses nodes.
//
// Run: bun test ts/search-index.test.ts   (wired into the Cake `Test` gate via the `WebTsTest`
//      task — see build.cs)

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { type BoardSearchIndex, ensureStemmersLoaded, matchingNodeIds, stem, tokens } from "./search-index.ts";

// board-search-stem-lookup, lazy load: stem() now throws unless ensureStemmersLoaded() already
// resolved (see search-index.ts's own comment — the dynamic import that keeps the stemmer out of
// the global site.js bundle). A top-level await here blocks the rest of THIS module's synchronous
// body — including every test() registration below — until the (real, not test-doubled) stemmer
// chunk has loaded, so every test body (which runs later, once the runner starts executing
// registered tests) finds it ready.
await ensureStemmersLoaded();

interface Fixture {
	stems: { word: string; stem: string }[];
	tokenize: { text: string; tokens: string[] }[];
}

// Walk up from this file until tests/fixtures/board-search-stem-fixture.json is found — the same
// "walk up from a known location" idiom BoardSearchStemFixtureTests.cs uses (from the test bin,
// via AppContext.BaseDirectory), just anchored at this source file instead since bun/node run TS
// directly with no separate build output directory.
function loadFixture(): Fixture {
	let dir = dirname(fileURLToPath(import.meta.url));
	for (let i = 0; i < 20; i++) {
		const candidate = join(dir, "tests", "fixtures", "board-search-stem-fixture.json");
		try {
			return JSON.parse(readFileSync(candidate, "utf-8")) as Fixture;
		} catch {
			const parent = dirname(dir);
			if (parent === dir) break;
			dir = parent;
		}
	}
	throw new Error("tests/fixtures/board-search-stem-fixture.json not found walking up from search-index.test.ts");
}

const fixture = loadFixture();

test("fixture: is non-empty (a truncated/misnamed file must not pass vacuously)", () => {
	assert.ok(fixture.stems.length > 0);
	assert.ok(fixture.tokenize.length > 0);
});

for (const { word, stem: expected } of fixture.stems) {
	test(`stem(${word}) === ${expected} (fixture parity with TokenStemmer.Stem)`, () => {
		assert.equal(stem(word), expected);
	});
}

for (const { text, tokens: expected } of fixture.tokenize) {
	test(`tokens(${text}) === [${expected.join(",")}] (fixture parity with FtsQuery.Tokens)`, () => {
		assert.deepEqual(tokens(text), expected);
	});
}

// --- matchingNodeIds: the actual search behavior the fixture parity exists to protect ---

function indexOf(nodes: { id: string; text: string }[]): BoardSearchIndex {
	const ids = nodes.map((n) => n.id);
	const body: Record<string, number[]> = {};
	const add = (key: string, idx: number): void => {
		if (!body[key]) body[key] = [];
		body[key].push(idx);
	};
	nodes.forEach((n, i) => {
		for (const tok of tokens(n.text)) add(stem(tok), i);
		add(n.id.toLowerCase(), i); // mirrors the server's raw-key entry
	});
	return { ids, body, title: {} };
}

test("owner acceptance example: query 'деплой' finds a node whose body says 'деплоем'", () => {
	const index = indexOf([{ id: "n1", text: "мы обновили сервис деплоем вчера" }]);
	const matched = matchingNodeIds(index, "деплой");
	assert.ok(matched?.has("n1"), "стем('деплой')='депл' must prefix-match стем('деплоем')='депло'");
});

test("multi-word query ANDs across words — a node must contain every word to match", () => {
	const index = indexOf([
		{ id: "both", text: "log rotation and wal pragma tuning" },
		{ id: "logOnly", text: "log rotation only" },
	]);
	const matched = matchingNodeIds(index, "log wal");
	assert.ok(matched?.has("both"));
	assert.ok(!matched?.has("logOnly"), "a node missing one AND word must not match");
});

test("empty query matches everything (returns null — the caller's 'no text filter' signal)", () => {
	const index = indexOf([{ id: "n1", text: "anything" }]);
	assert.equal(matchingNodeIds(index, ""), null);
	assert.equal(matchingNodeIds(index, "   "), null);
});

test("a slug is findable by any single segment and by the whole hyphenated key", () => {
	const index = indexOf([
		{ id: "board-search-stem-lookup", text: "board search stem lookup task title" },
		{ id: "other", text: "unrelated card" },
	]);
	for (const q of ["board", "search", "stem", "lookup", "board-search-stem-lookup"]) {
		const matched = matchingNodeIds(index, q);
		assert.ok(matched?.has("board-search-stem-lookup"), `query "${q}" should find the slugged node`);
	}
});
