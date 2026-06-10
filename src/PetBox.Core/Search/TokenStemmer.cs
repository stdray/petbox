using Snowball;

namespace PetBox.Core.Search;

// Per-token snowball stemming with SCRIPT routing — the app-level answer to FTS5 having
// no russian stemmer (spec: search-lexical-multilingual). Content here is mixed ru/en
// inside one document (Russian prose full of English identifiers), so language is decided
// per TOKEN by its script, not per document: Cyrillic → russian, Latin → english.
// Extending to another language = one more entry in the script map; every stemmer ships
// in the same managed package (libstemmer.net — the official snowball C# backend).
//
// Used symmetrically on both sides of the lexical floor:
//   index — SqliteFtsIndex appends each token's distinct stem as shadow text;
//   query — FtsQuery widens each token to (raw* OR stem*).
// The stems match the episodic tier's DuckDB stemmer='russian', so both tiers agree on
// normal forms.
public static class TokenStemmer
{
	// Snowball stemmer instances are stateful buffers — not thread-safe. Per-thread
	// instances keep Stem() lock-free on the hot read path.
	[ThreadStatic] static RussianStemmer? _russian;
	[ThreadStatic] static EnglishStemmer? _english;

	// The stem of `token` (already lowercased), or the token itself when no stemmer
	// claims its script or stemming is a no-op.
	public static string Stem(string token)
	{
		foreach (var ch in token)
		{
			if (ch is >= 'а' and <= 'я' or 'ё')
				return (_russian ??= new RussianStemmer()).Stem(token);
			if (ch is >= 'a' and <= 'z')
				return (_english ??= new EnglishStemmer()).Stem(token);
		}
		return token; // digits / other scripts — pass through
	}

	// Distinct stems of `text`'s tokens that DIFFER from their source token — the shadow
	// terms an FTS document needs so a stemmed query token can land on it. Empty when
	// nothing stems (pure identifiers/digits).
	public static string ShadowTerms(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return string.Empty;
		HashSet<string>? shadow = null;
		foreach (var token in FtsQuery.Tokens(text))
		{
			var stem = Stem(token);
			if (stem != token)
				(shadow ??= new HashSet<string>(StringComparer.Ordinal)).Add(stem);
		}
		return shadow is null ? string.Empty : string.Join(' ', shadow);
	}
}
