using System.Globalization;
using System.Text.Json;
using LinqToDB;
using LinqToDB.SqlQuery;
using PetBox.Log.Core.Metrics;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Tracing;

namespace PetBox.Log.Core.Query;

// [Sql.Expression]-mapped helpers backing the KQL string and datetime functions.
// Every method carries BOTH a SQLite translation (the attribute) and a real C# body: linq2db uses the
// attribute when the query runs as SQLite SQL (record/`where` context), while the in-memory paths
// (row context / LINQ-to-Objects differential) invoke the body directly as a plain method — outside
// linq2db entirely. Every mapping is ServerSideOnly = true: translation must happen ON the server or
// fail loudly — ServerSideOnly=false would let linq2db silently fall back to CLIENT-side evaluation,
// materializing the whole table to run the expression in memory. The typed string→value conversions
// (tolong/todouble/tobool/todatetime) that SQLite's CAST cannot express faithfully, and the regex
// surfaces (`matches regex`, `extract`, and the lowered `has`/`has_cs`), map to NATIVE per-dialect SQL
// via [Sql.Expression] — sqlean's regexp_* on SQLite (loaded per connection by LoadSqleanRegexpInterceptor),
// DuckDB's regexp_*/TRY_CAST — so those bodies never run in-memory and throw. No .NET scalar UDFs remain
// in the KQL path.
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

	// --- `matches regex` (+ the lowered `has`/`has_cs`). SQLite has no built-in regex, so this maps to
	// NATIVE per-dialect regex: sqlean's regexp_like on SQLite (loaded per connection by
	// LoadSqleanRegexpInterceptor), DuckDB's regexp_matches. Both take (subject, pattern) and return a
	// boolean; a NULL subject already yields 0/FALSE and the IFNULL/COALESCE pins that belt-and-suspenders.
	// `has`/`has_cs` carry NO dedicated function — KqlScalar.HasFn lowers them at COMPILE time to a
	// boundary regex over THIS shim (`(^|[^\p{L}\p{N}])term([^\p{L}\p{N}]|$)`, `(?i)` for the
	// case-insensitive `has`), so they ride this native SQL on both dialects. SQL-only: the body never
	// runs in-memory (single-SQL-path engine) and throws if invoked. ---

	[Sql.Expression(ProviderName.SQLite, "IFNULL(regexp_like({0}, {1}), 0)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	[Sql.Expression(ProviderName.DuckDB, "COALESCE(regexp_matches({0}, {1}), FALSE)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool MatchesRegex(string? s, string pattern) =>
		throw new NotSupportedException("KqlSqlExpressions.MatchesRegex is SQL-only (native per-dialect regexp_like/regexp_matches)");

	// --- string transforms: tolower / toupper / substring / strcat / extract. lower/upper/substr/||
	// map straight to SQLite; extract maps to native per-dialect regex capture (see Extract). ---

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

	// extract(regex, captureGroup, source) → the captured group's text, or "" when the regex does not
	// match or the group did not participate (Kusto returns empty string, not null). NATIVE per-dialect
	// regex capture — sqlean's regexp_capture(source, pattern, group) and DuckDB's
	// regexp_extract(source, pattern, group): NOTE the source-FIRST arg order, so the template maps
	// {2}=source, {0}=pattern, {1}=group. sqlean returns NULL on no-match → IFNULL(...,'') restores
	// Kusto's empty string; DuckDB returns '' natively (group 0 = whole match). SQL-only body: never runs
	// in-memory and throws if invoked.
	[Sql.Expression(ProviderName.SQLite, "IFNULL(regexp_capture({2}, {0}, {1}), '')", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	[Sql.Expression(ProviderName.DuckDB, "regexp_extract({2}, {0}, {1})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static string Extract(string? pattern, long captureGroup, string? source) =>
		throw new NotSupportedException("KqlSqlExpressions.Extract is SQL-only (native per-dialect regexp_capture/regexp_extract)");

	// 1-based index of the first ORDINAL occurrence of `needle` in `s` (0 when absent or `s` is null),
	// matching SQLite's builtin instr. Backs the SQL translation of `parse` (the star-free literal/position
	// matcher — KqlTransformer.BuildParseCaptures reproduces MatchParse's IndexOf via this + substr). C#
	// body mirrors the builtin exactly (IFNULL(...,0) folds the null arg so both pipeline positions agree).
	[Sql.Expression("IFNULL(instr({0}, {1}), 0)", ServerSideOnly = true)]
	public static long Instr(string? s, string needle) =>
		s is null ? 0 : s.IndexOf(needle, StringComparison.Ordinal) + 1;

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

	// .NET tick of the unix epoch (1970-01-01 UTC). bin(datetime) buckets are anchored at .NET tick 0
	// (year 1), NOT the epoch, so the epoch offset is folded in to match the in-memory BinDateTime exactly.
	const long UnixEpochTicks = 621355968000000000L;

	// bin(datetime, timespan) in the epoch-ms/SQL domain: floor the instant to a bucket of `stepTicks`
	// ABSOLUTE .NET ticks — identical to the in-memory BinDateTime (`v.Ticks / step.Ticks * step.Ticks`),
	// whose buckets are anchored at tick 0, so a step that does not divide the epoch offset still agrees
	// with Kusto. Input/output are epoch-ms (long, logical DateTime — converted once at materialization).
	// The caller (KqlScalar.Bin) guarantees stepTicks > 0 and a whole-millisecond step, so the epoch-ms
	// round-trip is lossless (E and floored are both multiples of TicksPerMillisecond).
	[Sql.Expression("((({0} * 10000 + 621355968000000000) / {1}) * {1} - 621355968000000000) / 10000", ServerSideOnly = true)]
	public static long BinDateTimeMs(long ms, long stepTicks) =>
		((ms * TimeSpan.TicksPerMillisecond + UnixEpochTicks) / stepTicks * stepTicks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond;

	// bin(value, step) for INTEGER values: floor(value/step)*step toward NEGATIVE INFINITY (Kusto/KQL
	// semantics, NOT trunc-toward-zero). Expressed via SQLite's non-negative-remainder identity
	// `value - ((value % step + step) % step)` — a single translatable arithmetic expression that agrees
	// on both providers even for negatives (SQLite `%`/`/` truncate toward zero). Unlike the old private
	// in-memory-only helper, this translates to SQL so it works as a `summarize … by bin(col, n)` GROUP BY
	// key (not just a client-evaluated projection). `step > 0` is the caller's contract (a positive literal
	// in practice); over SQLite a non-positive step yields NULL rather than throwing, so the C# body keeps
	// the loud guard for the in-memory path.
	[Sql.Expression("({0} - ((({0} % {1}) + {1}) % {1}))", ServerSideOnly = true)]
	public static long BinLong(long value, long step)
	{
		if (step <= 0)
			throw new UnsupportedKqlException("bin() step must be positive");
		var q = value / step;
		if (value % step != 0 && value < 0)
			q--; // floor toward negative infinity, not truncate toward zero
		return q * step;
	}

	// bin(value, step) for REAL values: floor(value/step)*step. SQLite CAST(real AS INTEGER) truncates
	// toward zero, so the floor is `trunc(q) - (q<0 AND q not integral ? 1 : 0)`; multiplied back by step.
	// Same SQL-translatable / dual-body rationale as BinLong.
	[Sql.Expression("((CAST({0} / {1} AS INTEGER) - (CASE WHEN {0} / {1} < 0 AND {0} / {1} <> CAST({0} / {1} AS INTEGER) THEN 1 ELSE 0 END)) * {1})", ServerSideOnly = true)]
	public static double BinDouble(double value, double step)
	{
		if (step <= 0)
			throw new UnsupportedKqlException("bin() step must be positive");
		return Math.Floor(value / step) * step;
	}

	// mv-expand array source: the JSON-array TEXT of `value` (a bag-extracted value or a whole column), or
	// '[]' when it is NOT a JSON array (null / missing / a scalar / an object). Fed to json_each, so '[]'
	// explodes to ZERO rows — the exact Kusto drop semantics for a non-array/absent value. Malformed-SAFE:
	// json_valid GATES json_type via a nested CASE (SQLite's AND is not short-circuit, and json_type on a
	// non-JSON scalar like a plain string raises 'malformed JSON'), so json_type only ever sees valid JSON.
	// SQL-only (mv-expand runs entirely in SQL); the C# body is never invoked in-memory.
	// `value` is object? (not string?) so a mv-expand of ANY column type binds — json_valid/json_type
	// inspect whatever affinity the value has (a non-array number/object → '[]' → 0 rows), no C# cast needed.
	[Sql.Expression(
		"(CASE WHEN json_valid({0}) THEN (CASE WHEN json_type({0}) = 'array' THEN {0} ELSE '[]' END) ELSE '[]' END)",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static string JsonArrayText(object? value) =>
		throw new NotSupportedException("KqlSqlExpressions.JsonArrayText is SQL-only (mv-expand array explode)");

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
	// nullable value types. They map to NATIVE per-dialect SQL via [Sql.Expression] rather than a bare
	// CAST because SQLite's CAST is NOT faithful here: CAST('abc' AS INTEGER) is 0 (not NULL) and
	// CAST('12x' AS INTEGER) is 12. SQLite gates a plain CAST behind a sqlean regexp_like well-formedness
	// check (regexp_* loaded per connection by LoadSqleanRegexpInterceptor); DuckDB uses TRY_CAST /
	// TRY_CAST-to-timestamp, which return NULL on malformed input natively. The portable contract
	// deliberately DROPS .NET-specific parsing (thousands separators, non-ISO datetime forms) for
	// cross-dialect parity. Numeric-to-numeric conversions (e.g. toint(Level)) don't need a function — a
	// direct Expr.Convert translates to a CAST that agrees with the C# truncating cast — so only the
	// string-input parse lives here. SQL-only: the bodies never run in-memory (single-SQL-path engine)
	// and throw if invoked. ---

	[Sql.Expression(ProviderName.SQLite, @"(CASE WHEN regexp_like({0}, '^\s*[+-]?[0-9]+\s*$') THEN CAST({0} AS INTEGER) ELSE NULL END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "TRY_CAST(trim({0}) AS BIGINT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? ParseLong(string? s) =>
		throw new NotSupportedException("KqlSqlExpressions.ParseLong is SQL-only (native per-dialect typed conversion)");

	[Sql.Expression(ProviderName.SQLite, @"(CASE WHEN regexp_like({0}, '^\s*[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?\s*$') THEN CAST({0} AS REAL) ELSE NULL END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "TRY_CAST({0} AS DOUBLE)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? ParseDouble(string? s) =>
		throw new NotSupportedException("KqlSqlExpressions.ParseDouble is SQL-only (native per-dialect typed conversion)");

	// Accepts the textual forms "true"/"false" (from the in-memory json_extract, which renders a JSON
	// boolean as its raw text) AND "1"/"0" (from SQLite's json_extract, which yields INTEGER 1/0 for a
	// JSON boolean). Handling both keeps `tobool(Properties.X)` identical whether the value arrived as
	// "1"/"0" or "true"/"false" — the CI true/false plus the '1'/'0' json_extract-integer-boolean bridge
	// documented above and preserved in the CASE below.
	[Sql.Expression(ProviderName.SQLite, "(CASE lower(trim({0})) WHEN 'true' THEN 1 WHEN '1' THEN 1 WHEN 'false' THEN 0 WHEN '0' THEN 0 ELSE NULL END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "(CASE lower(trim({0})) WHEN 'true' THEN TRUE WHEN '1' THEN TRUE WHEN 'false' THEN FALSE WHEN '0' THEN FALSE ELSE NULL END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static bool? ParseBool(string? s) =>
		throw new NotSupportedException("KqlSqlExpressions.ParseBool is SQL-only (native per-dialect typed conversion)");

	// todatetime parses an ISO-8601 string to epoch-MILLISECONDS (UTC — the SQL/record instant
	// representation). Assumes UTC for an unspecified offset and normalizes to UTC, matching how
	// timestamps are stored. SQLite's unixepoch(text,'subsec') yields fractional epoch SECONDS or NULL on
	// non-ISO-8601 input, and NULL propagates through the round/CAST (SQLite 3.50.4 in-bundle supports
	// 'subsec'). DuckDB: epoch_ms(TRY_CAST({0} AS TIMESTAMPTZ)) — ⚠ this REQUIRES `SET TimeZone='UTC'` at
	// the DuckDB connection init so unspecified-offset strings read as UTC (matching the SQLite/AssumeUtc
	// contract). DuckDB is not wired/active yet; when the future DuckDB wave wires connection init, that
	// TimeZone pragma MUST be set for this expression to honor the UTC contract.
	[Sql.Expression(ProviderName.SQLite, "CAST(round(unixepoch({0}, 'subsec') * 1000) AS INTEGER)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "epoch_ms(TRY_CAST({0} AS TIMESTAMPTZ))", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? ParseDateTimeMs(string? s) =>
		throw new NotSupportedException("KqlSqlExpressions.ParseDateTimeMs is SQL-only (native per-dialect typed conversion)");

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

	// MetricTypeName (metrics): the CASE-mapped discriminator name (0=Gauge … 4=Summary) — the metrics
	// analog of SpanKindName/SpanStatusName, keeping a pre-split `where TypeName == 'Histogram'` /
	// `order by TypeName` SQL-translatable; the C# body delegates to the same canonical map the streamed
	// row shape uses so the SQL and in-memory paths agree exactly.
	[Sql.Expression("(CASE {0} WHEN 0 THEN 'Gauge' WHEN 1 THEN 'Sum' WHEN 2 THEN 'Histogram' WHEN 3 THEN 'ExponentialHistogram' WHEN 4 THEN 'Summary' ELSE 'Unknown' END)", ServerSideOnly = true)]
	public static string MetricTypeName(int type) => MetricPointTypeNames.ToName(type);

	// The unified metric Value: COALESCE(ValueDouble, ValueLong) as a nullable double, so a Gauge/Sum
	// point exposes ONE numeric column regardless of which arm (double / int64) carried the value. NULL
	// when neither arm is set (a histogram/summary point). CAST the long arm to REAL so both arms share
	// the double result type on the SQL side, matching the double? the streamed row and C# body produce.
	[Sql.Expression("COALESCE({0}, CAST({1} AS REAL))", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? MetricValue(double? valueDouble, long? valueLong) =>
		valueDouble ?? (valueLong.HasValue ? valueLong.Value : (double?)null);
}

