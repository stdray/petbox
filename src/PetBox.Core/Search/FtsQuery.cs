using System.Text.RegularExpressions;

namespace PetBox.Core.Search;

// Shared FTS5 query builder. Lenient MATCH expression: Unicode word tokens
// (letters/digits — Latin, Cyrillic, …), prefix-matched (tok*) and ANDed. An FTS5
// unicode61 table case-folds and strips diacritics, so the query tokenizer must NOT
// drop non-ASCII: a `[a-z0-9]` class would silently discard a Russian query and return
// nothing (the bug fixed in memory/tasks; lifted here so every Class-A index shares the
// fix — spec: search-lexical-multilingual). Prefix-* also softens the lack of stemming
// for ru/en. Returns null when the query has no searchable tokens.
public static partial class FtsQuery
{
	public static string? BuildMatch(string? query)
	{
		if (string.IsNullOrWhiteSpace(query)) return null;
		var tokens = WordToken().Matches(query.ToLowerInvariant()).Select(m => m.Value + "*");
		var joined = string.Join(' ', tokens);
		return joined.Length == 0 ? null : joined;
	}

	[GeneratedRegex(@"[\p{L}\p{Nd}]+")]
	private static partial Regex WordToken();
}
