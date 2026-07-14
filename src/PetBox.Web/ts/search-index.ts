import { newStemmer } from "snowball-stemmers";

// board-search-stem-lookup: the client half of the board search stem lookup. Server side
// (PetBox.Web/Search/BoardSearchIndexBuilder.cs) builds `{stem -> node indices}` from
// PetBox.Core.Search.TokenStemmer/FtsQuery.Tokens and serves it from TaskBoard.cshtml.cs's
// OnGetSearchIndexAsync (`?handler=SearchIndex`, ETag'd on the board's own version cursor,
// server-cached — see that handler's own comment for why it's a separate cacheable resource and
// not embedded in the page). This file re-implements the SAME tokenizer + the SAME per-token
// script-routed snowball stemmer in TS (via `snowball-stemmers`, a faithful JS port of the
// official snowball.tartarus.org algorithms — the same source family libstemmer.net/Snowball.net
// wraps for the C# side), so a query stemmed here lands on the stems the server indexed.
//
// THE RISK this whole card exists to manage: the two stemmers disagree on one rule and search
// silently misses. tests/fixtures/board-search-stem-fixture.json is the ONE fixture both
// search-index.test.ts (this file) and BoardSearchStemFixtureTests.cs (the C# side) assert
// against — a divergence fails the gate on both.

const russian = newStemmer("russian");
const english = newStemmer("english");

// Mirrors PetBox.Core.Search.TokenStemmer.Stem exactly: per-CHARACTER script routing (first
// Cyrillic character seen -> russian stemmer, first a-z -> english), not per-document — content
// here is mixed ru/en prose full of English identifiers, so the token itself decides. A token
// with neither (pure digits, an identifier like "OTLP" after lowercasing still has a-z... digits
// alone, or another script) passes through unchanged.
export function stem(token: string): string {
	for (const ch of token) {
		if ((ch >= "а" && ch <= "я") || ch === "ё") return russian.stem(token);
		if (ch >= "a" && ch <= "z") return english.stem(token);
	}
	return token;
}

// Mirrors PetBox.Core.Search.FtsQuery.Tokens exactly: lowercased Unicode word tokens (letters +
// decimal digits — `\p{L}` / `\p{Nd}`, matching the C# regex `[\p{L}\p{Nd}]+`). Splits on
// hyphen/underscore (neither is `\p{L}`/`\p{Nd}`) — a slug like "board-search-stem-lookup"
// tokenizes into ["board","search","stem","lookup"], never the whole string; see
// matchingNodeIds' raw-key leg below for how the whole slug still gets found.
export function tokens(text: string): readonly string[] {
	return text.toLowerCase().match(/[\p{L}\p{Nd}]+/gu) ?? [];
}

// Mirrors FtsQuery.BuildMatch's single-letter-token rule: a length-1 token (almost always a
// preposition/conjunction — «в», «и», "a") must not become a MANDATORY AND term when longer
// tokens are present, or a query like «сессия в архиве» would wrongly require a literal "в" in
// the body; an all-short query (an initialism) keeps them, since dropping everything would leave
// no query at all.
function queryTokens(query: string): readonly string[] {
	const toks = tokens(query);
	return toks.some((t) => t.length > 1) ? toks.filter((t) => t.length > 1) : toks;
}

// The lookup shape TaskBoard.cshtml.cs's OnGetSearchIndexAsync returns (camelCase — default
// System.Text.Json policy). `ids[i]` is the NodeId a stem's index `i` refers to; `body`/`title`
// map a stem (or, per node, its raw lowercased Key — see below) to the node indices that stem.
export interface BoardSearchIndex {
	readonly ids: readonly string[];
	readonly body: Readonly<Record<string, readonly number[]>>;
	readonly title: Readonly<Record<string, readonly number[]>>;
}

// All node indices whose lookup carries a key starting with `prefix`. PREFIX match, not exact
// equality — this is what reproduces the server's own `(token* OR stem*)` FTS behavior without
// shipping raw text: a shorter Russian wordform's stem is very often a PREFIX of a longer form's
// stem (ru "деплой"->"депл", "деплоем"->"депло" — "депло".startsWith("депл")), so prefix-over-
// stems recovers the cross-wordform match the owner's acceptance example asks for ("деплой" must
// find a node whose body says "деплоем"). The accepted tradeoff (owner decision): a mid-word
// substring no longer matches — only a match anchored at the STEM's start does.
function idsForPrefix(map: Readonly<Record<string, readonly number[]>>, prefix: string): Set<number> {
	const out = new Set<number>();
	if (!prefix) return out;
	for (const key in map) {
		if (!key.startsWith(prefix)) continue;
		for (const idx of map[key] ?? []) out.add(idx);
	}
	return out;
}

function intersect(a: Set<number>, b: Set<number>): Set<number> {
	const out = new Set<number>();
	for (const x of a) if (b.has(x)) out.add(x);
	return out;
}

// The full set of matching NodeIds for a free-text query against `index`, or null when the query
// carries no searchable tokens at all (caller's cue to treat it as "no text filter" — same as an
// empty query). Two paths, UNIONED (either is enough — this can only ADD recall, never narrow):
//   - per-word AND: each query word is stemmed, prefix-matched against the lookup, and the
//     per-word id sets are INTERSECTED — multi-word queries narrow, exactly like the server's
//     ANDed FTS match expression.
//   - whole-slug: the RAW query (lowercased, trimmed, hyphens/underscores intact, NOT split into
//     words) is also prefix-matched directly — this is what makes pasting an exact/partial slug
//     ("board-search-stem-lookup") land precisely on that one node even when its individual
//     segments ("board","search",…) are common words shared with many other cards (owner
//     addendum: the whole key must be findable as a unit, not just via its segments).
export function matchingNodeIds(index: BoardSearchIndex, query: string): ReadonlySet<string> | null {
	const raw = query.trim().toLowerCase();
	const words = queryTokens(query);
	if (words.length === 0 && !raw) return null;

	let wordResult: Set<number> | null = null;
	for (const w of words) {
		const ids = idsForPrefix(index.body, stem(w));
		wordResult = wordResult === null ? ids : intersect(wordResult, ids);
		if (wordResult.size === 0) break;
	}

	const slugResult = idsForPrefix(index.body, raw);
	const union = new Set<number>([...(wordResult ?? []), ...slugResult]);
	if (wordResult === null && slugResult.size === 0) return null;

	const ids = new Set<string>();
	for (const idx of union) {
		const id = index.ids[idx];
		if (id !== undefined) ids.add(id);
	}
	return ids;
}
