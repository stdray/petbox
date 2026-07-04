using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.SqlQuery;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Tracing;

namespace PetBox.Log.Core.Query;

// [Sql.Expression]/[Sql.Function]-mapped helpers backing the KQL string and datetime functions.
// Every method carries BOTH a SQLite translation (the attribute) and a real C# body: linq2db uses the
// attribute when the query runs as SQLite SQL (record/`where` context), while the in-memory paths
// (row context / LINQ-to-Objects differential) invoke the body directly as a plain method — outside
// linq2db entirely. Every mapping is ServerSideOnly = true: translation must happen ON the server or
// fail loudly — ServerSideOnly=false would let linq2db silently fall back to CLIENT-side evaluation,
// materializing the whole table to run the expression in memory. Functions that SQLite cannot express
// with built-ins (token `has`, regex) are [Sql.Function]s bound to per-connection scalar functions
// registered by RegisterKqlFunctionsInterceptor; their C# body is the single source of truth that
// both the registered SQLite function and the in-memory path call.
public static class KqlSqlExpressions
{
	// Bag lookup via SQLite's BUILTIN json functions. The path is always a compile-time constant built
	// by JsonPath below — a double-QUOTED label ('$."petbox.request_chars"'), inside which SQLite
	// treats '.' as a literal key character, so flat dotted OTLP keys resolve natively with no per-row
	// managed calls. Keys are normalized (KqlPropertyKeys) at the write AND search boundaries, which
	// is what makes the quoted-label embedding watertight (no quotes/backslashes/control chars can
	// reach the path literal).
	//
	// The SQL shape forces the in-memory VALUE REPRESENTATION (JsonGet below: always text or NULL):
	// raw json_extract returns a JSON number with INTEGER/REAL affinity and a JSON boolean as 0/1, so
	// `where Status == "200"` was TRUE post-split (text bag) but silently FALSE pre-split (integer =
	// 'text' never matches in SQLite) — confirmed live, the worst divergence class. CAST(... AS TEXT)
	// fixes numbers; booleans need json_type() to render 'true'/'false' exactly like GetRawText().
	// The C# body parses the same two path forms the engine emits (quoted label / plain '$.key') and
	// does the identical flat lookup for the in-memory differential path.
	[Sql.Expression("(CASE json_type({0}, {1}) WHEN 'true' THEN 'true' WHEN 'false' THEN 'false' ELSE CAST(json_extract({0}, {1}) AS TEXT) END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? JsonExtract(string? json, string path) =>
		InMemoryJsonExtract(json, path);

	// The '$."key"' path literal for a NORMALIZED key (callers pass KqlPropertyKeys.Normalize output
	// only, so the quoted label needs no further escaping).
	public static string JsonPath(string normalizedKey) => "$.\"" + normalizedKey + "\"";

	static string? InMemoryJsonExtract(string? json, string? path)
	{
		if (string.IsNullOrEmpty(path) || !path.StartsWith("$.", StringComparison.Ordinal))
			return null;
		var key = path[2..];
		// The quoted-label form JsonPath emits: strip the surrounding quotes (normalized keys contain
		// no inner quotes or escapes, so this is exact).
		if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
			key = key[1..^1];
		return JsonGet(json, key);
	}

	// Flat bag lookup under an already-NORMALIZED key — a plain method (NOT a registered SQL function;
	// the SQL path rides the builtin json_extract with the quoted-label path above). Serves the
	// post-split row context and the transformer's in-memory extractors, and is the single source of
	// truth for what a lookup returns: a JSON string's text, a number in SQLite's canonical text form
	// (see NumberText), a bool's 'true'/'false', else null.
	public static string? JsonGet(string? json, string key)
	{
		if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
			return null;
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(key, out var prop))
				return null;
			return prop.ValueKind switch
			{
				JsonValueKind.String => prop.GetString(),
				JsonValueKind.Null or JsonValueKind.Undefined => null,
				// NOT GetRawText: the SQL side renders numbers through SQLite's CAST, which CANONICALIZES
				// the spelling ('2.50' → '2.5', '1e3' → '1000.0'); raw text here would diverge by pipeline
				// position for non-canonical source spellings. Render like SQLite instead.
				JsonValueKind.Number => NumberText(prop),
				_ => prop.GetRawText(),
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	// A JSON number exactly as SQLite's CAST(json_extract(...) AS TEXT) renders it: an integer keeps
	// its plain decimal form (INTEGER affinity); a real prints in SQLite's "%!.15g" shortest form —
	// always with a decimal point (integral reals get '.0', e-notation mantissas too: '1.0e+20').
	// The common forms (trailing zeros, integral reals, plain ints, e-notation) are pinned against
	// real SQLite by SqliteKqlIntegrationTests.NumberTextForms_AgreeAcrossPipelinePositions; extreme
	// magnitudes may still diverge in the last %.15g rounding digits (accepted residual edge, see the
	// NOTE there).
	static string NumberText(JsonElement number)
	{
		if (number.TryGetInt64(out var l))
			return l.ToString(CultureInfo.InvariantCulture);
		var d = number.GetDouble();
		var s = d.ToString("G15", CultureInfo.InvariantCulture);
		var e = s.IndexOf('E');
		if (e >= 0)
		{
			var mantissa = s[..e];
			if (!mantissa.Contains('.'))
				mantissa += ".0";
			return mantissa + "e" + s[(e + 1)..];
		}
		return s.Contains('.') ? s : s + ".0";
	}

	// --- string predicates: startswith / endswith (+ _cs). SQLite LIKE is case-insensitive for
	// ASCII only; we avoid LIKE (and its wildcard-escaping) entirely and compare fixed-length
	// substrings, folding ASCII case via lower() for the case-insensitive variants (documented
	// ASCII-only folding — matches .NET OrdinalIgnoreCase for the ASCII data logs carry). ---

	[Sql.Expression("substr(lower({0}), 1, length(lower({1}))) = lower({1})", ServerSideOnly = true)]
	public static bool StartsWithI(string? s, string needle) =>
		s != null && s.StartsWith(needle, StringComparison.OrdinalIgnoreCase);

	[Sql.Expression("substr({0}, 1, length({1})) = {1}", ServerSideOnly = true)]
	public static bool StartsWithCs(string? s, string needle) =>
		s != null && s.StartsWith(needle, StringComparison.Ordinal);

	[Sql.Expression("(length({0}) >= length({1}) AND substr(lower({0}), length({0}) - length({1}) + 1) = lower({1}))", ServerSideOnly = true)]
	public static bool EndsWithI(string? s, string needle) =>
		s != null && s.EndsWith(needle, StringComparison.OrdinalIgnoreCase);

	[Sql.Expression("(length({0}) >= length({1}) AND substr({0}, length({0}) - length({1}) + 1) = {1})", ServerSideOnly = true)]
	public static bool EndsWithCs(string? s, string needle) =>
		s != null && s.EndsWith(needle, StringComparison.Ordinal);

	// --- honest token `has` / `has_cs` and `matches regex`. SQLite has no term-splitting or regex
	// built in, so these are [Sql.Function]s bound to per-connection scalar functions (see
	// RegisterKqlFunctionsInterceptor) whose implementation IS the body below; the differential
	// (in-memory) path invokes the same body. `has` matches a whole term delimited by non-
	// alphanumeric characters (or string ends), NOT a substring. ---

	[Sql.Function("kql_has", ServerSideOnly = true)]
	public static bool Has(string? s, string term) => HasTerm(s, term, caseSensitive: false);

	[Sql.Function("kql_has_cs", ServerSideOnly = true)]
	public static bool HasCs(string? s, string term) => HasTerm(s, term, caseSensitive: true);

	[Sql.Function("kql_matches_regex", ServerSideOnly = true)]
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

	[Sql.Expression("lower({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.IfAnyParameterNullable)]
	public static string? ToLower(string? s) => s?.ToLowerInvariant();

	[Sql.Expression("upper({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.IfAnyParameterNullable)]
	public static string? ToUpper(string? s) => s?.ToUpperInvariant();

	// KQL substring(source, start[, length]). Non-negative start/length matches SQLite substr and
	// .NET Substring exactly; out-of-range is clamped to the empty string. Negative start/length —
	// KQL clamps them to 0, NOT SQLite's count-from-the-end — so the SQL translation clamps with
	// max(...,0) to agree with the C# body (SubstringImpl) on either pipeline path.
	[Sql.Expression("substr({0}, max({1}, 0) + 1)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? Substring2(string? s, long start) => SubstringImpl(s, start, null);

	[Sql.Expression("substr({0}, max({1}, 0) + 1, max({2}, 0))", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
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
	[Sql.Expression("(IFNULL({0}, '') || IFNULL({1}, ''))", ServerSideOnly = true)]
	public static string StrCat2(string? a, string? b) => (a ?? "") + (b ?? "");

	// extract(regex, captureGroup, source) → the captured group's text, or "" when the regex does
	// not match or the group did not participate (Kusto returns empty string, not null).
	[Sql.Function("kql_extract", ServerSideOnly = true)]
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

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of day') AS INTEGER) * 1000)", ServerSideOnly = true)]
	public static long StartOfDayMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc));
	}

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of day', '-' || strftime('%w', {0} / 1000, 'unixepoch') || ' days') AS INTEGER) * 1000)", ServerSideOnly = true)]
	public static long StartOfWeekMs(long ms)
	{
		var d = FromUnixMs(ms);
		var startOfDay = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
		return ToUnixMs(startOfDay.AddDays(-(int)startOfDay.DayOfWeek));
	}

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of month') AS INTEGER) * 1000)", ServerSideOnly = true)]
	public static long StartOfMonthMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of year') AS INTEGER) * 1000)", ServerSideOnly = true)]
	public static long StartOfYearMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	// Calendar-field extraction for datetime_diff's calendar parts (year/quarter/month).
	[Sql.Expression("CAST(strftime('%Y', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true)]
	public static long YearOfMs(long ms) => FromUnixMs(ms).Year;

	[Sql.Expression("CAST(strftime('%m', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true)]
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

	[Sql.Function("kql_tolong", ServerSideOnly = true)]
	public static long? ParseLong(string? s) =>
		long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

	[Sql.Function("kql_todouble", ServerSideOnly = true)]
	public static double? ParseDouble(string? s) =>
		double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v)
			? v
			: null;

	// Accepts the textual forms "true"/"false" (from the in-memory json_extract, which renders a JSON
	// boolean as its raw text) AND "1"/"0" (from SQLite's json_extract, which yields INTEGER 1/0 for a
	// JSON boolean). Handling both keeps `tobool(Properties.X)` identical whether the predicate runs as
	// SQLite SQL (pre-split `where`) or in-memory (post-split), which otherwise disagree by pipeline
	// position — the SQL path fed "1"/"0" and rejected it, the in-memory path saw "true"/"false".
	[Sql.Function("kql_tobool", ServerSideOnly = true)]
	public static bool? ParseBool(string? s)
	{
		if (bool.TryParse(s, out var v))
			return v;
		return s switch { "1" => true, "0" => false, _ => (bool?)null };
	}

	// todatetime parses an ISO-8601 string to epoch-ms (the SQL/record instant representation).
	// Assumes UTC for an unspecified offset and normalizes to UTC, matching how timestamps are stored.
	[Sql.Function("kql_todatetime", ServerSideOnly = true)]
	public static long? ParseDateTimeMs(string? s) =>
		DateTime.TryParse(s, CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
			? ToUnixMs(dt)
			: null;

	// tostring for integer/boolean scalars — a faithful CAST/CASE on the SQL side mirrored by the C#
	// body. (String arguments pass through unchanged in the compiler and never reach here.)
	[Sql.Expression("CAST({0} AS TEXT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.IfAnyParameterNullable)]
	public static string LongToString(long v) => v.ToString(CultureInfo.InvariantCulture);

	[Sql.Expression("(CASE WHEN {0} THEN 'true' ELSE 'false' END)", ServerSideOnly = true)]
	public static string BoolToString(bool v) => v ? "true" : "false";

	// Null-propagating variants: a nullable numeric/boolean scalar (the result of a typed conversion
	// like toint/tobool) stringifies to null when null, else to its text — so tostring() composes over
	// the conversion functions. CAST(NULL AS TEXT) is NULL in SQLite, matching the C# body.
	[Sql.Expression("CAST({0} AS TEXT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? LongToStringN(long? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : null;

	[Sql.Expression("(CASE WHEN {0} IS NULL THEN NULL WHEN {0} THEN 'true' ELSE 'false' END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? BoolToStringN(bool? v) => v.HasValue ? (v.Value ? "true" : "false") : null;

	// Nullable epoch-ms → nullable DateTime, bridging todatetime into the in-memory/row instant
	// representation (DateTime?), where null means "not a valid datetime".
	public static DateTime? FromUnixMsN(long? ms) => ms.HasValue ? FromUnixMs(ms.Value) : null;

	// --- computed name columns: LevelName (events), KindName / StatusName (spans). The CASE mapping
	// keeps a pre-split `where LevelName == 'Error'` / `order by KindName` SQL-translatable (a raw call
	// to the *Names.ToName map has no linq2db translation and would fail at enumeration on a real log
	// DB); the C# body delegates to the same canonical map the streamed row shape uses, so the SQL and
	// in-memory paths agree exactly. ---

	[Sql.Expression("(CASE {0} WHEN 0 THEN 'Verbose' WHEN 1 THEN 'Debug' WHEN 2 THEN 'Information' WHEN 3 THEN 'Warning' WHEN 4 THEN 'Error' WHEN 5 THEN 'Fatal' ELSE 'Unknown' END)", ServerSideOnly = true)]
	public static string LevelName(int level) => LogLevelNames.ToName(level);

	[Sql.Expression("(CASE {0} WHEN 0 THEN 'Internal' WHEN 1 THEN 'Server' WHEN 2 THEN 'Client' WHEN 3 THEN 'Producer' WHEN 4 THEN 'Consumer' ELSE 'Unknown' END)", ServerSideOnly = true)]
	public static string SpanKindName(int kind) => SpanKindNames.ToName(kind);

	[Sql.Expression("(CASE {0} WHEN 0 THEN 'Unset' WHEN 1 THEN 'Ok' WHEN 2 THEN 'Error' ELSE 'Unknown' END)", ServerSideOnly = true)]
	public static string SpanStatusName(int code) => SpanStatusNames.ToName(code);
}

