namespace PetBox.Web.Mcp;

// Card work/unknown-param-silently-ignored-breaks-renames-quietly. Parameter-name suggestions for
// McpUnknownParameterFilter — a DIFFERENT domain from NamespaceSuggest, deliberately not merged
// into it and not "fixed" by loosening NamespaceSuggest's own threshold:
//   - NamespaceSuggest ranks an OPEN-ENDED, user-named domain (memory stores, project keys) where a
//     prefix relation is often a DELIBERATE derivation (`notes-archive` is not a typo of `notes`),
//     so it deliberately carries no prefix leg (see its own header).
//   - Here the candidate set is one tool's own CLOSED schema (5-20 declared parameter names), and
//     the renames this card was filed over are characteristically prefix-preserving
//     (`under`->`underNode`, `key`->`keyValue`) or short-hop typos/transpositions
//     (`boadr`->`board`). A prefix leg is safe here precisely because there is no derived-sibling
//     risk NamespaceSuggest guards against: two DIFFERENT parameters of the SAME tool sharing a
//     prefix relation would itself be a schema naming collision that wouldn't ship.
//   - ROOT CAUSE this class fixes: NamespaceSuggest.Nearest's budget (`max(1, min(3, len/3))`) is
//     tuned for its own long-tail domain and is too tight for short closed-set names — verified
//     empirically, not assumed. `under` (len 5) -> budget 1, but the length gap to `underNode` is
//     4, so Distance's own `|len(a)-len(b)| > budget` guard discards the candidate before scoring
//     it. `boadr` -> `board` (budget 1, equal length) needs distance 2 for the adjacent-character
//     transposition (Levenshtein has no single-op swap), so it too is discarded. Both renames the
//     card exists for fell through the SAME reused function silently — the prior green test
//     (`UnknownTopLevelKey_SuggestsNearestKnownName_WhenClose`, `boad`->`board`) only ever hit the
//     one input short enough to clear that budget, which is why it stayed green while the real
//     incident scenario produced no hint at all.
//
// TWO ARMS, because neither alone covers the renames this card was filed over:
//   - "near": prefix affinity (either direction) plus Levenshtein — the core is REUSED from
//     NamespaceSuggest.Distance (not reimplemented), just called with a wider, purpose-tuned budget.
//   - "enumeration": the near arm cannot catch a rename sharing neither shape (`keys`->`nodes`,
//     `nodeId`->`hostId` — nothing edit-distance- or prefix-shaped links them). The caller's
//     tool-list snapshot is stale by construction (that is this card's whole premise), so it
//     cannot re-read the schema itself; the error text has to hand over the current accepted set
//     outright. Always included, independent of whether "near" found anything — this is what
//     makes ANY rename self-correcting, not just the ones lucky enough to look like a typo.
static class ParamNameSuggest
{
	// Cap on how many accepted names ride the error text verbatim. Every real tool schema in this
	// codebase sits well under this (the card's own estimate: "5-20 names" for a closed parameter
	// set); the cap exists so a pathological or future wide flat-parameter tool can't turn a
	// rejection message into a schema dump. Beyond the cap: the first MaxListed in schema-declared
	// order (matching how the tool's own docs list them, not an arbitrary re-sort) plus a count of
	// the remainder.
	const int MaxListed = 15;

	public static string Describe(IReadOnlyList<string> known) =>
		known.Count <= MaxListed
			? string.Join(", ", known)
			: string.Join(", ", known.Take(MaxListed)) + $", and {known.Count - MaxListed} more";

	// Nearest known parameter names to `name`, closest/most-relevant first. Prefix matches always
	// outrank distance matches (a prefix relation on a closed 5-20 name set is strong signal — see
	// class comment), ties broken ordinally for determinism.
	public static IReadOnlyList<string> Nearest(string name, IReadOnlyList<string> known, int take = 3)
	{
		var prefix = known
			.Where(k => k.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
				name.StartsWith(k, StringComparison.OrdinalIgnoreCase))
			.OrderBy(k => k, StringComparer.Ordinal);

		// Budget floor of 2, not 1: a single adjacent-character transposition (`boadr`->`board`)
		// costs 2 under plain Levenshtein (no single-op swap), so a floor of 1 — NamespaceSuggest's
		// own floor, tuned for its own domain — would silently miss the exact scenario this class
		// exists for. Still scales up with name length for longer names, same shape as NamespaceSuggest.
		var budget = Math.Max(2, name.Length / 3);
		var distance = known
			.Select(k => (Key: k, Score: NamespaceSuggest.Distance(name, k, budget)))
			.Where(x => x.Score <= budget)
			.OrderBy(x => x.Score)
			.ThenBy(x => x.Key, StringComparer.Ordinal)
			.Select(x => x.Key);

		return prefix.Concat(distance).Distinct(StringComparer.OrdinalIgnoreCase).Take(take).ToList();
	}
}

// Card work/unknown-param-curated-hints. Two frequent misses where SIMILARITY (above) gives no
// useful hint, or the WRONG one, because the domain knowledge isn't in the schema at all — it is in
// a decision the schema can't express:
//   - `limit` on session_search: session-search-page-width-param-name deliberately did NOT add
//     `limit` — the tool has TWO independent page-size knobs (`sessions`, `hitsPerSession`), and a
//     single `limit` would have to pick one. Nearest() finds nothing close (no shared prefix, and
//     the edit distance to every real name is well past budget), so without a curated entry the
//     caller gets only the bare "Accepted parameters" list and has to guess which one plays
//     `limit`'s role.
//   - `usageSource` on a WRITE verb: the parameter is real, just not on THIS tool — it exists only
//     on read verbs (`tasks_node_get`, `memory_search`, …), where it tags who triggered the read
//     (deliberate vs. machine) for usage accounting. A write records no impressions, so it was never
//     given the parameter. That asymmetry is invisible from "unknown parameter" alone.
//
// Deliberately still a REJECTION, exactly like every other offender here: a curated entry only
// changes the MESSAGE, never accepts the call or silently maps the name onto something else.
//
// A curated hint REPLACES that one offender's similarity computation rather than sitting next to
// it (see McpUnknownParameterFilter.Unknown) — `query` prefix-matches `q` closely enough that
// Nearest() would offer "Did you mean 'q'?" on its own, and printing both would just repeat the
// same fix twice.
static class CuratedParamHint
{
	// (does this offender's leaf name match?, does it fire in this tool/scope?) -> the hint text.
	// Scoped by CONTENT where possible (does this scope declare `q`?) rather than a hardcoded tool
	// list, so the `query` entry tracks memory_search/tasks_search/session_search/comments_search/
	// config_binding_search — every *_search verb with a free-text query — without naming them, and
	// never fires on health_search, which has no `q` at all. `limit`/session_search has no schema
	// signal to key off (the absence of a knob can't be read from a knob), so that one entry is
	// named explicitly by tool.
	static readonly (Func<string, bool> Leaf, Func<string, List<string>, bool> Fires, string Text)[] Table =
	[
		(Leaf: static l => l == "limit", Fires: static (tool, _) => tool == "session_search",
			Text: "session_search has no 'limit' — its two page-size knobs are 'sessions' (how many " +
				"sessions to hydrate and search inside) and 'hitsPerSession' (hits returned per session)."),

		(Leaf: static l => l == "usageSource", Fires: static (_, _) => true,
			Text: "'usageSource' exists only on read verbs — it tags who triggered the read " +
				"(deliberate vs. machine) for usage accounting. A write records no impressions, so " +
				"it does not accept the parameter at all; drop it."),

		(Leaf: static l => l == "query", Fires: static (_, scope) => scope.Contains("q"),
			Text: "the search text parameter is 'q' on every *_search tool, not 'query'."),
	];

	public static string? Lookup(string tool, string leaf, List<string> scope) =>
		Table.Where(e => e.Leaf(leaf) && e.Fires(tool, scope)).Select(e => e.Text).FirstOrDefault();
}
