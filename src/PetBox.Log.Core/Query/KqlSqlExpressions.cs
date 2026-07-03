using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.SqlQuery;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Tracing;

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
	// .NET Substring exactly; out-of-range is clamped to the empty string. Negative start/length —
	// KQL clamps them to 0, NOT SQLite's count-from-the-end — so the SQL translation clamps with
	// max(...,0) to agree with the C# body (SubstringImpl) on either pipeline path.
	[Sql.Expression("substr({0}, max({1}, 0) + 1)", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? Substring2(string? s, long start) => SubstringImpl(s, start, null);

	[Sql.Expression("substr({0}, max({1}, 0) + 1, max({2}, 0))", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
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

	// --- datetime instant conversion. A wall-clock instant is stored/compared as epoch-ms (long) in
	// the SQL/record context and as DateTime in the in-memory/row context; these bridge the two. ---

	public static long ToUnixMs(DateTime dt)
	{
		var utc = dt.Kind == DateTimeKind.Unspecified
			? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
			: dt.ToUniversalTime();
		return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
	}

	public static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

	// --- startof* on epoch-ms. SQLite via strftime(...,'unixepoch',...); the C# body mirrors it for
	// the in-memory path. Week starts Sunday (Kusto): start-of-day minus the day-of-week (%w, 0=Sun). ---

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of day') AS INTEGER) * 1000)", ServerSideOnly = false)]
	public static long StartOfDayMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc));
	}

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of day', '-' || strftime('%w', {0} / 1000, 'unixepoch') || ' days') AS INTEGER) * 1000)", ServerSideOnly = false)]
	public static long StartOfWeekMs(long ms)
	{
		var d = FromUnixMs(ms);
		var startOfDay = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
		return ToUnixMs(startOfDay.AddDays(-(int)startOfDay.DayOfWeek));
	}

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of month') AS INTEGER) * 1000)", ServerSideOnly = false)]
	public static long StartOfMonthMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of year') AS INTEGER) * 1000)", ServerSideOnly = false)]
	public static long StartOfYearMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	// Calendar-field extraction for datetime_diff's calendar parts (year/quarter/month).
	[Sql.Expression("CAST(strftime('%Y', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = false)]
	public static long YearOfMs(long ms) => FromUnixMs(ms).Year;

	[Sql.Expression("CAST(strftime('%m', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = false)]
	public static long MonthOfMs(long ms) => FromUnixMs(ms).Month;

	// --- typed conversions: tostring / toint|tolong / todouble / tobool / todatetime. Malformed or
	// missing input yields NULL (Kusto's conversion semantics), so the string-parse helpers return
	// nullable value types. They are registered SQLite scalar functions (see RegisterKqlFunctions-
	// Interceptor) rather than a bare CAST because SQLite's CAST is NOT faithful here: CAST('abc' AS
	// INTEGER) is 0 (not NULL) and CAST('12x' AS INTEGER) is 12. Each C# body is the single source of
	// truth the registered SQLite function and the in-memory differential path both call, so a
	// `where toint(Properties.Status) >= 500` compares numerically and identically on either path.
	// Numeric-to-numeric conversions (e.g. toint(Level)) don't need a function — a direct
	// Expr.Convert translates to a CAST that agrees with the C# truncating cast — so only the
	// string-input parse lives here. ---

	[Sql.Function("kql_tolong", ServerSideOnly = false)]
	public static long? ParseLong(string? s) =>
		long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

	[Sql.Function("kql_todouble", ServerSideOnly = false)]
	public static double? ParseDouble(string? s) =>
		double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v)
			? v
			: null;

	// Accepts the textual forms "true"/"false" (from the in-memory json_extract, which renders a JSON
	// boolean as its raw text) AND "1"/"0" (from SQLite's json_extract, which yields INTEGER 1/0 for a
	// JSON boolean). Handling both keeps `tobool(Properties.X)` identical whether the predicate runs as
	// SQLite SQL (pre-split `where`) or in-memory (post-split), which otherwise disagree by pipeline
	// position — the SQL path fed "1"/"0" and rejected it, the in-memory path saw "true"/"false".
	[Sql.Function("kql_tobool", ServerSideOnly = false)]
	public static bool? ParseBool(string? s)
	{
		if (bool.TryParse(s, out var v))
			return v;
		return s switch { "1" => true, "0" => false, _ => (bool?)null };
	}

	// todatetime parses an ISO-8601 string to epoch-ms (the SQL/record instant representation).
	// Assumes UTC for an unspecified offset and normalizes to UTC, matching how timestamps are stored.
	[Sql.Function("kql_todatetime", ServerSideOnly = false)]
	public static long? ParseDateTimeMs(string? s) =>
		DateTime.TryParse(s, CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
			? ToUnixMs(dt)
			: null;

	// tostring for integer/boolean scalars — a faithful CAST/CASE on the SQL side mirrored by the C#
	// body. (String arguments pass through unchanged in the compiler and never reach here.)
	[Sql.Expression("CAST({0} AS TEXT)", ServerSideOnly = false, IsNullable = Sql.IsNullableType.IfAnyParameterNullable)]
	public static string LongToString(long v) => v.ToString(CultureInfo.InvariantCulture);

	[Sql.Expression("(CASE WHEN {0} THEN 'true' ELSE 'false' END)", ServerSideOnly = false)]
	public static string BoolToString(bool v) => v ? "true" : "false";

	// Null-propagating variants: a nullable numeric/boolean scalar (the result of a typed conversion
	// like toint/tobool) stringifies to null when null, else to its text — so tostring() composes over
	// the conversion functions. CAST(NULL AS TEXT) is NULL in SQLite, matching the C# body.
	[Sql.Expression("CAST({0} AS TEXT)", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? LongToStringN(long? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : null;

	[Sql.Expression("(CASE WHEN {0} IS NULL THEN NULL WHEN {0} THEN 'true' ELSE 'false' END)", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? BoolToStringN(bool? v) => v.HasValue ? (v.Value ? "true" : "false") : null;

	// Nullable epoch-ms → nullable DateTime, bridging todatetime into the in-memory/row instant
	// representation (DateTime?), where null means "not a valid datetime".
	public static DateTime? FromUnixMsN(long? ms) => ms.HasValue ? FromUnixMs(ms.Value) : null;

	// --- computed name columns: LevelName (events), KindName / StatusName (spans). The CASE mapping
	// keeps a pre-split `where LevelName == 'Error'` / `order by KindName` SQL-translatable (a raw call
	// to the *Names.ToName map has no linq2db translation and would fail at enumeration on a real log
	// DB); the C# body delegates to the same canonical map the streamed row shape uses, so the SQL and
	// in-memory paths agree exactly. ---

	[Sql.Expression("(CASE {0} WHEN 0 THEN 'Verbose' WHEN 1 THEN 'Debug' WHEN 2 THEN 'Information' WHEN 3 THEN 'Warning' WHEN 4 THEN 'Error' WHEN 5 THEN 'Fatal' ELSE 'Unknown' END)", ServerSideOnly = false)]
	public static string LevelName(int level) => LogLevelNames.ToName(level);

	[Sql.Expression("(CASE {0} WHEN 0 THEN 'Internal' WHEN 1 THEN 'Server' WHEN 2 THEN 'Client' WHEN 3 THEN 'Producer' WHEN 4 THEN 'Consumer' ELSE 'Unknown' END)", ServerSideOnly = false)]
	public static string SpanKindName(int kind) => SpanKindNames.ToName(kind);

	[Sql.Expression("(CASE {0} WHEN 0 THEN 'Unset' WHEN 1 THEN 'Ok' WHEN 2 THEN 'Error' ELSE 'Unknown' END)", ServerSideOnly = false)]
	public static string SpanStatusName(int code) => SpanStatusNames.ToName(code);
}

