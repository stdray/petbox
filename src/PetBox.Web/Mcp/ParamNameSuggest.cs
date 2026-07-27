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
