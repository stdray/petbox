using System.Globalization;

namespace PetBox.Log.Core.Query;

// The single key-normalization rule for the flat Properties/Attributes bags, applied at BOTH
// boundaries:
//   WRITE  — before the bag JSON is stored (PropertiesJsonSerializer for CLEF/seq events and the
//            self-log, the OTLP logs/traces parsers for otel attributes), via NameAllocator below;
//   SEARCH — before a KQL lookup builds its json_extract path or in-memory flat lookup. The seam is
//            exactly TWO owners: ScalarContext.ResolveProperties (all expression paths — where/
//            project/extend/order) and KqlTransformer.BagValueExtractor (all materialized-row paths —
//            summarize-by / distinct / mv-expand / join keys).
// Normalizing on both sides means a stored key and a requested key always meet in the same form.
//
// The rule: double quotes, backslashes and control characters are replaced with '_'. That makes any
// key safe to embed verbatim in a double-quoted SQLite JSON-path label — json_extract(bag, '$."k"')
// — which is what lets flat DOTTED keys (petbox.request_chars, http.route) resolve natively: inside
// a quoted label SQLite treats '.' as a literal key character, not a path separator. Dots are
// deliberately KEPT — they are the whole point (OTLP names are dotted).
//
// Rows stored BEFORE this rule existed were written unnormalized — acceptable: normalization only
// alters keys containing path-hostile characters (quotes / backslashes / control), which do not
// occur in real OTLP/CLEF keys; every realistic key is a fixed point of Normalize. A legacy row
// whose stored key DOES contain a hostile character is unreachable from KQL (the search side always
// normalizes the requested key) — an accepted trade-off, deliberately not coded around: the log
// store is append-only with retention, so unnormalized rows age out and the store self-heals.
public static class KqlPropertyKeys
{
	public static string Normalize(string key)
	{
		var hostile = false;
		foreach (var c in key)
			if (IsHostile(c))
			{
				hostile = true;
				break;
			}
		if (!hostile)
			return key;

		return string.Create(key.Length, key, static (dst, src) =>
		{
			for (var i = 0; i < src.Length; i++)
				dst[i] = IsHostile(src[i]) ? '_' : src[i];
		});
	}

	static bool IsHostile(char c) => c is '"' or '\\' || char.IsControl(c);

	// WRITE-boundary name assignment for the keys of ONE bag. Normalize is non-injective ('a"b' and
	// 'a\b' both become 'a_b'), so a bare per-key Normalize could emit duplicate-key JSON or silently
	// drop a collider. Policy: the FIRST original to claim a normalized name keeps it; each subsequent
	// DISTINCT original that collides gets a deterministic '_2', '_3', … suffix in encounter order.
	// The SAME original key always maps to the same stored name (so a repeated OTLP attribute stays
	// last-wins in the producer's dictionary, exactly as before).
	public sealed class NameAllocator
	{
		readonly Dictionary<string, string> _byOriginal = new(StringComparer.Ordinal);
		readonly HashSet<string> _used = new(StringComparer.Ordinal);

		public string Assign(string originalKey)
		{
			if (_byOriginal.TryGetValue(originalKey, out var existing))
				return existing;
			var name = Normalize(originalKey);
			if (!_used.Add(name))
			{
				var i = 2;
				string candidate;
				do
				{
					candidate = name + "_" + i.ToString(CultureInfo.InvariantCulture);
					i++;
				}
				while (!_used.Add(candidate));
				name = candidate;
			}
			_byOriginal[originalKey] = name;
			return name;
		}
	}
}
