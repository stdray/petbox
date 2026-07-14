using PetBox.Core.Search;
using PetBox.Tasks.Contract;

namespace PetBox.Web.Search;

// board-search-stem-lookup: the {stem -> node indices} lookup ts/board.ts fetches (via
// TaskBoardModel.OnGetSearchIndexAsync) instead of the old per-card `data-search` attribute,
// which stamped title+key+FULL BODY+tags onto every card — the body shipped TWICE (rendered HTML
// + the attribute), 294KB gzip on the real `work` board. Reuses the EXISTING shared stemming
// utilities verbatim (owner's explicit ask) — TokenStemmer.Stem for the per-token script-routed
// snowball stem, FtsQuery.Tokens for the SAME tokenizer the FTS index/query builder already use
// (Unicode word tokens, hyphen/underscore-splitting — see the class comment on Index below for
// why the raw Key is ALSO indexed unstemmed).
//
// `ids` is the ordered NodeId list; `body`/`title` map each distinct stem to the (deduped, one
// entry per node) list of INDICES into `ids` — indices compress far better than repeating 32-hex
// NodeIds per stem/node pair (measured: 124KB gzip vs 294KB for the old blob, on 481 nodes /
// 11,105 stems). `title` (title+key only) is the smaller lookup a future "matched in body only"
// UI affordance can diff against `body` — built now because it's cheap (~18KB gzip), not wired
// into any UI yet.
public static class BoardSearchIndexBuilder
{
	public static BoardSearchIndex Build(IReadOnlyList<PlanNodeView> nodes)
	{
		var ids = new List<string>(nodes.Count);
		var body = new Dictionary<string, List<int>>(StringComparer.Ordinal);
		var title = new Dictionary<string, List<int>>(StringComparer.Ordinal);
		for (var i = 0; i < nodes.Count; i++)
		{
			var n = nodes[i];
			ids.Add(n.NodeId);
			Index(body, i, n.Title, n.Key, n.Body, string.Join(' ', n.Tags));
			Index(title, i, n.Title, n.Key);
		}
		return new BoardSearchIndex(ids, Freeze(body), Freeze(title));
	}

	// Indexes `parts` under this node's stemmed tokens PLUS one extra entry: the node's raw Key
	// (Slug), lowercased, UNSTEMMED and UNSPLIT. Owner addendum (board-search-stem-lookup): a slug
	// is hyphen/underscore-joined ("board-search-stem-lookup"), and FtsQuery.Tokens' word regex
	// (`[\p{L}\p{Nd}]+`) splits on both — so the WHOLE slug never survives tokenization as its own
	// token, only its segments do ("board","search","stem","lookup" — each independently findable
	// already, since they're regular stemmed entries). A search for the segments ANDs down to the
	// right node in practice, but pasting the exact full slug deserves an exact, unambiguous hit
	// even when its segments are common words shared with many other cards — hence this literal
	// raw-key entry, checked client-side as an ADDITIONAL (union, never narrowing) match path
	// alongside the per-word AND (ts/search-index.ts's matchingNodeIds).
	static void Index(Dictionary<string, List<int>> map, int idx, params string?[] parts)
	{
		var text = string.Join(' ', parts);
		foreach (var stem in FtsQuery.Tokens(text).Select(TokenStemmer.Stem).Distinct(StringComparer.Ordinal))
			Add(map, stem, idx);
		// parts[1] is always the node's Key by construction (see the two call sites above).
		var key = parts.Length > 1 ? parts[1] : null;
		if (!string.IsNullOrEmpty(key))
			Add(map, key.ToLowerInvariant(), idx);
	}

	static void Add(Dictionary<string, List<int>> map, string key, int idx)
	{
		if (!map.TryGetValue(key, out var list))
			map[key] = list = [];
		if (list.Count == 0 || list[^1] != idx) list.Add(idx);
	}

	static Dictionary<string, IReadOnlyList<int>> Freeze(Dictionary<string, List<int>> map) =>
		map.ToDictionary(kv => kv.Key, IReadOnlyList<int> (kv) => kv.Value, StringComparer.Ordinal);
}

// ids[i] is the NodeId a stem's index `i` refers to. body = stems of title+key+body+tags (the
// primary lookup ts/board.ts filters with); title = stems of title+key only (the smaller "matched
// in title" lookup — unused by any UI yet, built because it's cheap). Both dictionaries ALSO carry
// each node's raw lowercased Key as an extra unstemmed entry (see BoardSearchIndexBuilder.Index).
public sealed record BoardSearchIndex(
	IReadOnlyList<string> Ids,
	IReadOnlyDictionary<string, IReadOnlyList<int>> Body,
	IReadOnlyDictionary<string, IReadOnlyList<int>> Title);
