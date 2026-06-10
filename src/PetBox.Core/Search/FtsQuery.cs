using System.Text;
using System.Text.RegularExpressions;

namespace PetBox.Core.Search;

// Shared FTS5 query builder. Lenient MATCH expression: Unicode word tokens
// (letters/digits — Latin, Cyrillic, …), prefix-matched (tok*) and ANDed. An FTS5
// unicode61 table case-folds and strips diacritics, so the query tokenizer must NOT
// drop non-ASCII: a `[a-z0-9]` class would silently discard a Russian query and return
// nothing (the bug fixed in memory/tasks; lifted here so every Class-A index shares the
// fix — spec: search-lexical-multilingual).
//
// Wordform recall: each token whose snowball stem differs widens to `(tok* OR stem*)` —
// the stemmed leg matches both the document's shadow terms (new rows) and, for most
// Russian morphology, the raw text directly (a stem is usually a prefix of every
// wordform), so pre-stemming rows benefit too. Returns null when the query has no
// searchable tokens.
public static partial class FtsQuery
{
	public static string? BuildMatch(string? query)
	{
		if (string.IsNullOrWhiteSpace(query)) return null;
		var tokens = Tokens(query).ToList();
		// Single-letter tokens are almost always prepositions/conjunctions («в», «и», "a")
		// that AND would make mandatory and sink the query («сессия в архиве» must not
		// require a document containing «в»). Drop them while longer tokens remain; an
		// all-short query (e.g. an initialism) keeps them.
		if (tokens.Any(t => t.Length > 1))
			tokens.RemoveAll(t => t.Length == 1);
		var sb = new StringBuilder();
		foreach (var token in tokens)
		{
			// Explicit AND: fts5 allows implicit AND between bare terms but not after a
			// parenthesized group, so the join must be spelled out once groups appear.
			if (sb.Length > 0) sb.Append(" AND ");
			var stem = TokenStemmer.Stem(token);
			if (stem != token)
				sb.Append('(').Append(token).Append("* OR ").Append(stem).Append("*)");
			else
				sb.Append(token).Append('*');
		}
		return sb.Length == 0 ? null : sb.ToString();
	}

	// Lowercased unicode word tokens (letters/digits) — the one tokenizer both the query
	// builder and the index-side shadow stemming share.
	public static IEnumerable<string> Tokens(string text) =>
		WordToken().Matches(text.ToLowerInvariant()).Select(m => m.Value);

	[GeneratedRegex(@"[\p{L}\p{Nd}]+")]
	private static partial Regex WordToken();
}
