using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.SqlQuery;

namespace PetBox.Log.Core.Query;

// [Sql.Expression]/[Sql.Function]-mapped helpers backing the KQL string and datetime functions.
// Every method carries BOTH a SQLite translation (the attribute) and a real C# body, so the SAME
// method is exact whether it runs as SQLite SQL (record/`where` context, translated by linq2db) or
// in-memory (row context / LINQ-to-Objects differential path, where the body is invoked directly).
// Functions that SQLite cannot express with built-ins (token `has`, regex) are [Sql.Function]s bound
// to per-connection scalar functions registered by RegisterKqlFunctionsInterceptor; their C# body is
// the single source of truth that both the registered SQLite function and the in-memory path call.
public static class KqlSqlExpressions
{
	[Sql.Expression("json_extract({0}, {1})", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? JsonExtract(string? json, string path) =>
		InMemoryJsonExtract(json, path);

	static string? InMemoryJsonExtract(string? json, string? path)
	{
		if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path) || !path.StartsWith("$.", StringComparison.Ordinal))
			return null;
		var key = path[2..];
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(key, out var prop))
				return null;
			return prop.ValueKind switch
			{
				JsonValueKind.String => prop.GetString(),
				JsonValueKind.Null or JsonValueKind.Undefined => null,
				_ => prop.GetRawText(),
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	[Sql.Function("json_extract", ServerSideOnly = true)]
	public static string? JsonExtractScalar(string? column, string path) => throw new NotSupportedException();

	// --- string predicates: startswith / endswith (+ _cs). SQLite LIKE is case-insensitive for
	// ASCII only; we avoid LIKE (and its wildcard-escaping) entirely and compare fixed-length
	// substrings, folding ASCII case via lower() for the case-insensitive variants (documented
	// ASCII-only folding — matches .NET OrdinalIgnoreCase for the ASCII data logs carry). ---

	[Sql.Expression("substr(lower({0}), 1, length(lower({1}))) = lower({1})", ServerSideOnly = false)]
	public static bool StartsWithI(string? s, string needle) =>
		s != null && s.StartsWith(needle, StringComparison.OrdinalIgnoreCase);

	[Sql.Expression("substr({0}, 1, length({1})) = {1}", ServerSideOnly = false)]
	public static bool StartsWithCs(string? s, string needle) =>
		s != null && s.StartsWith(needle, StringComparison.Ordinal);

	[Sql.Expression("(length({0}) >= length({1}) AND substr(lower({0}), length({0}) - length({1}) + 1) = lower({1}))", ServerSideOnly = false)]
	public static bool EndsWithI(string? s, string needle) =>
		s != null && s.EndsWith(needle, StringComparison.OrdinalIgnoreCase);

	[Sql.Expression("(length({0}) >= length({1}) AND substr({0}, length({0}) - length({1}) + 1) = {1})", ServerSideOnly = false)]
	public static bool EndsWithCs(string? s, string needle) =>
		s != null && s.EndsWith(needle, StringComparison.Ordinal);

	// --- honest token `has` / `has_cs` and `matches regex`. SQLite has no term-splitting or regex
	// built in, so these are [Sql.Function]s bound to per-connection scalar functions (see
	// RegisterKqlFunctionsInterceptor) whose implementation IS the body below; the differential
	// (in-memory) path invokes the same body. `has` matches a whole term delimited by non-
	// alphanumeric characters (or string ends), NOT a substring. ---

	[Sql.Function("kql_has", ServerSideOnly = false)]
	public static bool Has(string? s, string term) => HasTerm(s, term, caseSensitive: false);

	[Sql.Function("kql_has_cs", ServerSideOnly = false)]
	public static bool HasCs(string? s, string term) => HasTerm(s, term, caseSensitive: true);

	[Sql.Function("kql_matches_regex", ServerSideOnly = false)]
	public static bool MatchesRegex(string? s, string pattern) =>
		s != null && Regex.IsMatch(s, pattern);

	static bool HasTerm(string? haystack, string term, bool caseSensitive)
	{
		if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(term))
			return false;
		var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		var from = 0;
		while (from <= haystack.Length - term.Length)
		{
			var idx = haystack.IndexOf(term, from, cmp);
			if (idx < 0)
				return false;
			var leftBoundary = idx == 0 || !IsTermChar(haystack[idx - 1]);
			var end = idx + term.Length;
			var rightBoundary = end == haystack.Length || !IsTermChar(haystack[end]);
			if (leftBoundary && rightBoundary)
				return true;
			from = idx + 1;
		}
		return false;
	}

	static bool IsTermChar(char c) => char.IsLetterOrDigit(c);

	// --- string transforms: tolower / toupper / substring / strcat / extract. lower/upper/substr/||
	// map straight to SQLite; extract needs regex → a registered scalar function (kql_extract). ---

	[Sql.Expression("lower({0})", ServerSideOnly = false, IsNullable = Sql.IsNullableType.IfAnyParameterNullable)]
	public static string? ToLower(string? s) => s?.ToLowerInvariant();

	[Sql.Expression("upper({0})", ServerSideOnly = false, IsNullable = Sql.IsNullableType.IfAnyParameterNullable)]
	public static string? ToUpper(string? s) => s?.ToUpperInvariant();

	// KQL substring(source, start[, length]). Non-negative start/length matches SQLite substr and
	// .NET Substring exactly; out-of-range is clamped to the empty string. (Negative start/length —
	// KQL's from-the-end semantics — are clamped to 0 here; documented, and not exercised.)
	[Sql.Expression("substr({0}, {1} + 1)", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? Substring2(string? s, long start) => SubstringImpl(s, start, null);

	[Sql.Expression("substr({0}, {1} + 1, {2})", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? Substring3(string? s, long start, long length) => SubstringImpl(s, start, length);

	static string? SubstringImpl(string? s, long start, long? length)
	{
		if (s is null)
			return null;
		if (start < 0)
			start = 0;
		if (start >= s.Length)
			return "";
		var available = s.Length - (int)start;
		var take = length is null ? available : Math.Max(0, Math.Min((int)length.Value, available));
		return s.Substring((int)start, take);
	}

	// strcat folds pairwise via ||; NULL operands render as empty (Kusto strcat coerces null → "").
	[Sql.Expression("(IFNULL({0}, '') || IFNULL({1}, ''))", ServerSideOnly = false)]
	public static string StrCat2(string? a, string? b) => (a ?? "") + (b ?? "");

	// extract(regex, captureGroup, source) → the captured group's text, or "" when the regex does
	// not match or the group did not participate (Kusto returns empty string, not null).
	[Sql.Function("kql_extract", ServerSideOnly = false)]
	public static string Extract(string? pattern, long captureGroup, string? source)
	{
		if (source is null || pattern is null)
			return "";
		var m = Regex.Match(source, pattern);
		if (!m.Success || captureGroup < 0 || captureGroup >= m.Groups.Count)
			return "";
		var g = m.Groups[(int)captureGroup];
		return g.Success ? g.Value : "";
	}
}

