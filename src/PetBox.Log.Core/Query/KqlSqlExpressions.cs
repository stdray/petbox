using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
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
	// DuckDB: json_extract_string yields the same VALUE REPRESENTATION as the SQLite CASE — canonical
	// numbers (2.50→2.5, 1e3→1000.0), bool→'true'/'false', null/missing→NULL — natively, so no CASE needed.
	// (The generic arm below stays the SQLite translation; the DuckDB-specific arm wins for DuckDB.)
	[Sql.Expression("(CASE json_type({0}, {1}) WHEN 'true' THEN 'true' WHEN 'false' THEN 'false' ELSE CAST(json_extract({0}, {1}) AS TEXT) END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "json_extract_string({0}, {1})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
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

	// `=~` / `!~`: case-INSENSITIVE string equality — the same lower() fold as StartsWithI/EndsWithI
	// (ASCII-only, documented there). SQLite's `=` on TEXT is BINARY/case-sensitive, so the fold is not
	// optional. A NULL operand makes the SQL predicate NULL (row excluded), which IS Kusto's rule for both
	// `=~` and `!~`; the negated spelling still goes through KqlScalar.Negated so the exclusion survives the
	// NOT (see Negated's comment).
	[Sql.Expression("(lower({0}) = lower({1}))", ServerSideOnly = true)]
	public static bool EqualsI(string? s, string? other) =>
		s != null && string.Equals(s, other, StringComparison.OrdinalIgnoreCase);

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

	// The null-propagating comparison the string shims above DO NOT universally give us: a `!<op>` must
	// EXCLUDE a null subject (Kusto: a null matches nothing, and so does its negation), but MatchesRegex
	// deliberately PINS a null subject to FALSE (IFNULL/COALESCE), so NOT(<a pinned FALSE>) would keep the
	// row. This explicit IS NOT NULL is what restores the rule for every negated string predicate.
	[Sql.Expression("({0} IS NOT NULL)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool IsNotNull(string? s) => s != null;

	// The mirror of IsNotNull, backing KQL's isnull().
	[Sql.Expression("({0} IS NULL)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool IsNull(string? s) => s == null;

	// KQL's isempty()/isnotempty(): "null OR empty string" — ONE shim per spelling rather than an
	// OR/AND composed out of IsNotNull + `= ''`, because the composed form leans on SQL's NULL-propagating
	// `=` for its answer (NULL = '' is NULL, and NOT(false) never sees it). The C# bodies are the in-memory
	// half of the same shim, as everywhere else in this file.
	[Sql.Expression("({0} IS NULL OR {0} = '')", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool IsEmpty(string? s) => string.IsNullOrEmpty(s);

	[Sql.Expression("({0} IS NOT NULL AND {0} <> '')", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool IsNotEmpty(string? s) => !string.IsNullOrEmpty(s);

	[Sql.Expression("upper({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.IfAnyParameterNullable)]
	public static string? ToUpper(string? s) => s?.ToUpperInvariant();

	// KQL substring(source, start[, length]). Non-negative start/length matches SQLite substr and
	// .NET Substring exactly; out-of-range is clamped to the empty string. Negative start/length —
	// KQL clamps them to 0, NOT SQLite's count-from-the-end — so the SQL translation clamps with
	// max(...,0) to agree with the C# body (SubstringImpl) on either pipeline path.
	// DuckDB `max` is aggregate-only → use `greatest` for the element-wise clamp (same semantics).
	[Sql.Expression("substr({0}, max({1}, 0) + 1)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "substr({0}, greatest({1}, 0) + 1)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? Substring2(string? s, long start) => SubstringImpl(s, start, null);

	[Sql.Expression("substr({0}, max({1}, 0) + 1, max({2}, 0))", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "substr({0}, greatest({1}, 0) + 1, greatest({2}, 0))", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
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
	// matcher — KqlTransformer.BuildParseCaptures reproduces that IndexOf via this + substr). C#
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
	// DuckDB `/` is FLOAT division → use `//` (integer div) so the epoch-ms result stays BIGINT; formula
	// is otherwise identical to the SQLite arm (whose `/` is integer div on integers).
	[Sql.Expression("((({0} * 10000 + 621355968000000000) / {1}) * {1} - 621355968000000000) / 10000", ServerSideOnly = true)]
	[Sql.Expression(ProviderName.DuckDB, "((({0} * 10000 + 621355968000000000) // {1}) * {1} - 621355968000000000) // 10000", ServerSideOnly = true)]
	public static long BinDateTimeMs(long ms, long stepTicks) =>
		((ms * TimeSpan.TicksPerMillisecond + UnixEpochTicks) / stepTicks * stepTicks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond;

	// bin(datetime, timespan) over a NULLABLE instant — the datetime counterpart of BinLongN/BinDoubleN, and
	// the shape todatetime() produces (every bag value is TEXT, so `bin(todatetime(Properties.X), 1h)` — THE
	// time-histogram idiom over the property bag — feeds a long? epoch-ms here; Kusto maps a malformed/missing
	// value to null). The SQL is the IDENTITY of the non-null arm: NULL propagates through the arithmetic on
	// both providers, so an unparseable timestamp lands in its OWN (null) bucket in `summarize … by bin(…)`
	// instead of failing the query — and it is NOT coalesced to the epoch, which would invent a 1970 bucket.
	// A separate NAME (not an overload) because the compiler resolves these by GetMethod(name).
	[Sql.Expression("((({0} * 10000 + 621355968000000000) / {1}) * {1} - 621355968000000000) / 10000", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "((({0} * 10000 + 621355968000000000) // {1}) * {1} - 621355968000000000) // 10000", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? BinDateTimeMsN(long? ms, long stepTicks) =>
		ms is { } v ? BinDateTimeMs(v, stepTicks) : null;

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

	// REAL-domain guard for arithmetic that promotes to double. linq2db prints a double CONSTANT with an
	// integral value as a bare integer literal (`100.0` → `100`), and both SQLite and DuckDB are dynamically
	// typed here: `100 * Id / 12000` then runs as INTEGER math and TRUNCATES (`100.0 * chars / 8441721` came
	// back 41 instead of 41.658…). A plain Expr.Convert doesn't help — linq2db elides it. So the double arm of
	// KqlScalar.Arithmetic wraps each operand in this explicit CAST, which pins the SQL operands to the real
	// domain (SQLite REAL / DuckDB DOUBLE — DuckDB's REAL is single-precision, hence the separate arm). The C#
	// body is the identity (NOT ServerSideOnly: casting to double is meaningful in memory too).
	[Sql.Expression(ProviderName.SQLite, "CAST({0} AS REAL)")]
	[Sql.Expression(ProviderName.DuckDB, "CAST({0} AS DOUBLE)")]
	public static double AsReal(double value) => value;

	// The null-propagating form (a converted property is double?): CAST(NULL) is NULL on both providers.
	[Sql.Expression(ProviderName.SQLite, "CAST({0} AS REAL)", IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "CAST({0} AS DOUBLE)", IsNullable = Sql.IsNullableType.Nullable)]
	public static double? AsRealN(double? value) => value;

	// bin(value, step) over a NULLABLE integer — the shape a typed conversion produces (toint/tolong of a
	// Properties string is long?, since a malformed/missing value is Kusto's null). Same identity, same SQL:
	// NULL propagates through the arithmetic on both providers, so a null value lands in its own (null) bucket
	// instead of failing the query or being silently coalesced to 0. A separate NAME (not an overload) because
	// the compiler resolves these by GetMethod(name).
	[Sql.Expression("({0} - ((({0} % {1}) + {1}) % {1}))", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? BinLongN(long? value, long? step) =>
		value is { } v && step is { } s ? BinLong(v, s) : null;

	// bin(value, step) for REAL values: floor(value/step)*step. SQLite CAST(real AS INTEGER) truncates
	// toward zero, so the floor is `trunc(q) - (q<0 AND q not integral ? 1 : 0)`; multiplied back by step.
	// Same SQL-translatable / dual-body rationale as BinLong.
	// DuckDB CAST(real AS INTEGER) ROUNDS (not truncates), so the SQLite trunc-based floor doesn't port —
	// use floor() directly for the toward-negative-infinity bucket.
	[Sql.Expression("((CAST({0} / {1} AS INTEGER) - (CASE WHEN {0} / {1} < 0 AND {0} / {1} <> CAST({0} / {1} AS INTEGER) THEN 1 ELSE 0 END)) * {1})", ServerSideOnly = true)]
	[Sql.Expression(ProviderName.DuckDB, "(floor({0} / {1}) * {1})", ServerSideOnly = true)]
	public static double BinDouble(double value, double step)
	{
		if (step <= 0)
			throw new UnsupportedKqlException("bin() step must be positive");
		return Math.Floor(value / step) * step;
	}

	// bin(value, step) over a NULLABLE real — the todouble() counterpart of BinLongN (null in → null bucket).
	[Sql.Expression("((CAST({0} / {1} AS INTEGER) - (CASE WHEN {0} / {1} < 0 AND {0} / {1} <> CAST({0} / {1} AS INTEGER) THEN 1 ELSE 0 END)) * {1})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "(floor({0} / {1}) * {1})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? BinDoubleN(double? value, double? step) =>
		value is { } v && step is { } s ? BinDouble(v, s) : null;

	// mv-expand array source: the JSON-array TEXT of `value` (a bag-extracted value or a whole column), or
	// '[]' when it is NOT a JSON array (null / missing / a scalar / an object). Fed to json_each, so '[]'
	// explodes to ZERO rows — the exact Kusto drop semantics for a non-array/absent value. Malformed-SAFE:
	// json_valid GATES json_type via a nested CASE (SQLite's AND is not short-circuit, and json_type on a
	// non-JSON scalar like a plain string raises 'malformed JSON'), so json_type only ever sees valid JSON.
	// SQL-only (mv-expand runs entirely in SQL); the C# body is never invoked in-memory.
	// `value` is object? (not string?) so a mv-expand of ANY column type binds — json_valid/json_type
	// inspect whatever affinity the value has (a non-array number/object → '[]' → 0 rows), no C# cast needed.
	// DuckDB: TRY_CAST({0} AS JSON) is NULL on malformed input → json_type(NULL)=NULL → COALESCE '[]', so the
	// whole thing is malformed-SAFE without SQLite's json_valid gate. Only an actual JSON ARRAY passes through.
	[Sql.Expression(
		"(CASE WHEN json_valid({0}) THEN (CASE WHEN json_type({0}) = 'array' THEN {0} ELSE '[]' END) ELSE '[]' END)",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	[Sql.Expression(ProviderName.DuckDB,
		"COALESCE((CASE WHEN json_type(TRY_CAST({0} AS JSON)) = 'ARRAY' THEN CAST({0} AS VARCHAR) ELSE '[]' END), '[]')",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static string JsonArrayText(object? value) =>
		throw new NotSupportedException("KqlSqlExpressions.JsonArrayText is SQL-only (mv-expand array explode)");

	// --- startof* on epoch-ms. SQLite via strftime(...,'unixepoch',...); the C# body mirrors it for
	// the in-memory path. Week starts Sunday (Kusto): start-of-day minus the day-of-week (%w, 0=Sun). ---

	// DuckDB: date_trunc over the instant (epoch_ms(ms) → TIMESTAMP), back to epoch-ms via epoch_ms(TIMESTAMP).
	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of day') AS INTEGER) * 1000)", ServerSideOnly = true)]
	[Sql.Expression(ProviderName.DuckDB, "epoch_ms(date_trunc('day', epoch_ms({0})))", ServerSideOnly = true)]
	public static long StartOfDayMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc));
	}

	// DuckDB: start-of-day minus dayofweek days (Kusto week = Sunday start; DuckDB dayofweek Sunday=0).
	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of day', '-' || strftime('%w', {0} / 1000, 'unixepoch') || ' days') AS INTEGER) * 1000)", ServerSideOnly = true)]
	[Sql.Expression(ProviderName.DuckDB, "epoch_ms(date_trunc('day', epoch_ms({0})) - to_days(CAST(dayofweek(epoch_ms({0})) AS INTEGER)))", ServerSideOnly = true)]
	public static long StartOfWeekMs(long ms)
	{
		var d = FromUnixMs(ms);
		var startOfDay = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
		return ToUnixMs(startOfDay.AddDays(-(int)startOfDay.DayOfWeek));
	}

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of month') AS INTEGER) * 1000)", ServerSideOnly = true)]
	[Sql.Expression(ProviderName.DuckDB, "epoch_ms(date_trunc('month', epoch_ms({0})))", ServerSideOnly = true)]
	public static long StartOfMonthMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	[Sql.Expression("(CAST(strftime('%s', {0} / 1000, 'unixepoch', 'start of year') AS INTEGER) * 1000)", ServerSideOnly = true)]
	[Sql.Expression(ProviderName.DuckDB, "epoch_ms(date_trunc('year', epoch_ms({0})))", ServerSideOnly = true)]
	public static long StartOfYearMs(long ms)
	{
		var d = FromUnixMs(ms);
		return ToUnixMs(new DateTime(d.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	// Calendar-field extraction for datetime_diff's calendar parts (year/quarter/month).
	[Sql.Expression("CAST(strftime('%Y', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true)]
	[Sql.Expression(ProviderName.DuckDB, "CAST(year(epoch_ms({0})) AS BIGINT)", ServerSideOnly = true)]
	public static long YearOfMs(long ms) => FromUnixMs(ms).Year;

	[Sql.Expression("CAST(strftime('%m', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true)]
	[Sql.Expression(ProviderName.DuckDB, "CAST(month(epoch_ms({0})) AS BIGINT)", ServerSideOnly = true)]
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
	// timestamps are stored. SQLite's unixepoch(text,'subsec') does NOT reject non-ISO input — it
	// silently returns a WRONG non-null instant for many malformed strings (e.g. bare numbers) — so a
	// sqlean regexp_like well-formedness gate (regexp_* loaded per connection by
	// LoadSqleanRegexpInterceptor) restricts input to the ISO-8601 subset (date-only, optional T/space
	// time separator, optional seconds/fractional, optional Z or ±HH[:]MM offset) and yields NULL for
	// anything else; unixepoch then computes epoch SECONDS at 'subsec' (MILLISECOND) precision on the gated
	// input (unspecified offset = UTC) — it ROUNDS to the ms internally — scaled to ms and CAST-truncated to
	// INTEGER (the CAST only guards float representation drift below the ms; SQLite 3.50.4 in-bundle supports
	// 'subsec'). A shaped-but-impossible date like
	// '2024-13-45' passes the regex but unixepoch returns NULL for it — fine. DuckDB:
	// epoch_ms(TRY_CAST({0} AS TIMESTAMPTZ)) — ⚠ this REQUIRES `SET TimeZone='UTC'` at the DuckDB
	// connection init so unspecified-offset strings read as UTC (matching the SQLite/AssumeUtc contract).
	// DuckDB is not wired/active yet; when the future DuckDB wave wires connection init, that TimeZone
	// pragma MUST be set for this expression to honor the UTC contract.
	// NB: the regex spells its digit counts as literal \d repetitions (\d\d\d\d not \d{4}) — linq2db's
	// [Sql.Expression] treats `{N}` in the template as an ARGUMENT placeholder, so a `{4}` quantifier would
	// be mis-parsed at query build; \d\d\d\d is the brace-free equivalent.
	[Sql.Expression(ProviderName.SQLite, @"(CASE WHEN regexp_like({0}, '^\s*\d\d\d\d-\d\d-\d\d([ T]\d\d:\d\d(:\d\d(\.\d+)?)?)?(Z|[+-]\d\d:?\d\d)?\s*$') THEN CAST(unixepoch({0}, 'subsec') * 1000 AS INTEGER) ELSE NULL END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
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

	// === string scalars: strlen / indexof / replace_string / trim,trim_start,trim_end / format_bytes /
	// isascii / isutf8. Contracts verified against the official Kusto docs (kusto/query/<fn>-function).
	// As everywhere in this file each method carries a [Sql.Expression] translation AND a real C# body:
	// the attribute is what the SQL path uses, the body is what the in-memory/row compile (logical typing)
	// and the differential tests run, and the two are pinned to each other by KqlScalarFunctionsBatchTests.
	// Null IN → null OUT everywhere (Kusto's null rule, and what length()/replace()/regexp_replace() already
	// do natively), so no CASE is needed for the null case. ---

	// strlen = length in CHARACTERS. Both dialects' length() counts Unicode CODE POINTS over a UTF-8
	// string, which is exactly Kusto's rule ("This function counts Unicode code points", e.g. 'C\u0327edilla'
	// is 8); .NET s.Length would count UTF-16 units, hence the rune count in the body.
	[Sql.Expression("length({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? StrLen(string? s) => s is null ? null : s.EnumerateRunes().Count();

	// indexof(source, match, start, length) — ZERO-based index of `match`, -1 when not found, NULL for an
	// invalid start/length (Kusto: null when start is negative after the end-offset, or length < -1).
	// `start` < 0 offsets from the END of source ("abs(start) steps from the end"); `length` = -1 means
	// unlimited. instr on SQLite and strpos on DuckDB are the SAME 1-based (haystack, needle) primitive —
	// DuckDB also accepts instr, so this is ONE portable expression. An empty `match` is NULL (Kusto does
	// not define it; the dialects disagree on instr(x, ''), so it is pinned away from the divergence).
	// The occurrence argument is NOT here: counting the Nth occurrence needs a per-row loop no dialect can
	// express, so the registry refuses it (see KqlScalarFunctions.IndexOf).
	[Sql.Expression(
		"(CASE WHEN length({1}) = 0 OR {3} < -1 THEN NULL"
		+ " WHEN (CASE WHEN {2} < 0 THEN length({0}) + {2} ELSE {2} END) < 0 THEN NULL"
		+ " WHEN instr(substr({0}, (CASE WHEN {2} < 0 THEN length({0}) + {2} ELSE {2} END) + 1, (CASE WHEN {3} = -1 THEN length({0}) ELSE {3} END)), {1}) = 0 THEN -1"
		+ " ELSE (CASE WHEN {2} < 0 THEN length({0}) + {2} ELSE {2} END) - 1"
		+ " + instr(substr({0}, (CASE WHEN {2} < 0 THEN length({0}) + {2} ELSE {2} END) + 1, (CASE WHEN {3} = -1 THEN length({0}) ELSE {3} END)), {1}) END)",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? IndexOf(string? s, string? match, long start, long length)
	{
		if (s is null || string.IsNullOrEmpty(match) || length < -1)
			return null;
		if (start < 0)
		{
			start += s.Length;
			if (start < 0)
				return null;
		}
		if (start >= s.Length)
			return -1;
		var window = length == -1
			? s[(int)start..]
			: s[(int)start..(int)Math.Min(s.Length, start + length)];
		var idx = window.IndexOf(match, StringComparison.Ordinal);
		return idx < 0 ? -1 : start + idx;
	}

	// replace_string(text, lookup, rewrite) — ALL matches, NON-overlapping (Kusto: "Matches don't
	// overlap"), which is exactly SQLite's/DuckDB's replace(text, lookup, rewrite). An empty lookup is a
	// no-op: .NET's string.Replace throws on "" while the SQL replaces nothing, and this keeps the two
	// sides identical.
	[Sql.Expression("replace({0}, {1}, {2})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? ReplaceString(string? text, string? lookup, string? rewrite) =>
		text is null || string.IsNullOrEmpty(lookup) || rewrite is null
			? text
			: text.Replace(lookup, rewrite, StringComparison.Ordinal);

	// trim_start / trim_end / trim take a REGEX (not a char set) and strip ALL leading / trailing / both
	// matches of it (kusto/query/trim-function: "Removes all leading and trailing matches of the specified
	// regular expression"). A single anchored regexp_replace cannot repeat, so the registry passes an
	// ALREADY-ANCHORED, already-quantified pattern — `^(?:P)+` / `(?:P)+$` — whose greedy repetition
	// consumes the same run of matches Kusto's loop would. Both dialects' regexp_replace replace matches of
	// the pattern with the replacement (DuckDB non-global by default, sqlean global — immaterial for an
	// ANCHORED pattern, which can only match at one place).
	[Sql.Expression("regexp_replace({0}, {1}, '')", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? RegexTrim(string? source, string pattern) =>
		source is null ? null : Regex.Replace(source, pattern, "", RegexOptions.CultureInvariant);

	// format_bytes: ONE rung of the unit ladder — the registry picks the divisor/unit (and folds the
	// magnitude CASE for auto-selection), so the only dialect surface left is printf. Verified identical on
	// SQLite and DuckDB (printf('%.1f', 10332.0/1024) = '10.1' on both). `format` is the compile-time
	// literal '%.{precision}f'; the C# body re-derives the precision from it so one literal serves both
	// sides.
	[Sql.Expression(ProviderName.SQLite, "(printf({1}, CAST({0} AS REAL)) || ' ' || {2})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "(printf({1}, CAST({0} AS DOUBLE)) || ' ' || {2})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? FormatBytesRung(double? size, string format, string unit)
	{
		if (size is not { } value)
			return null;
		var precision = format.Length >= 4 ? format[2..^1] : "0";
		var rounded = Math.Round(value, int.Parse(precision, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero);
		return rounded.ToString("F" + precision, CultureInfo.InvariantCulture) + " " + unit;
	}

	// isascii: "valid ASCII string" = every character is < 128 = the UTF-8 BYTE count equals the CHARACTER
	// count. SQLite spells the byte count octet_length, DuckDB spells it strlen (DuckDB's octet_length only
	// accepts BLOB/BIT). Null is not a valid ASCII string, so it pins to FALSE rather than NULL.
	[Sql.Expression(ProviderName.SQLite, "COALESCE(length({0}) = octet_length({0}), 0)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	[Sql.Expression(ProviderName.DuckDB, "COALESCE(length({0}) = strlen({0}), FALSE)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool IsAscii(string? s)
	{
		if (s is null)
			return false;
		foreach (var c in s)
			if (c > 0x7f)
				return false;
		return true;
	}

	// isutf8: every Kusto string is valid UTF-8 by construction (invalid input is replaced with U+FFFD at
	// ingestion), and both dialects STORE text as UTF-8, so the SQL side is "is there a value at all". The
	// body makes the same statement precisely: a string whose UTF-8 encoding is well-formed (a .NET string
	// carrying a lone surrogate would NOT be, and cannot be stored either).
	[Sql.Expression("({0} IS NOT NULL)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool IsUtf8(string? s)
	{
		if (s is null)
			return false;
		try
		{
			new UTF8Encoding(false, throwOnInvalidBytes: true).GetByteCount(s);
			return true;
		}
		catch (EncoderFallbackException)
		{
			return false;
		}
	}

	// === math scalars. Kusto's sign/round/floor/… are REAL-domain (double) functions; the arithmetic
	// guards below are not decoration — they are measured dialect divergences (SQLite returns NULL for
	// sqrt(-1)/ln(-1)/ln(0) and for pow(-8, 0.5), where DuckDB THROWS 'Out of Range Error' or returns NaN).
	// Pinning every domain edge to NULL keeps both backends identical AND matches Kusto's documented null
	// for log(negative|0). Every template is domain-guarded so DuckDB never raises. ---

	[Sql.Expression("abs({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? AbsLong(long? v) => v is { } x ? Math.Abs(x) : null;

	[Sql.Expression("abs({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? AbsDouble(double? v) => v is { } x ? Math.Abs(x) : null;

	[Sql.Expression("floor({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? Floor(double? v) => v is { } x ? Math.Floor(x) : null;

	[Sql.Expression("ceil({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? Ceiling(double? v) => v is { } x ? Math.Ceiling(x) : null;

	// round(number[, precision]) — Kusto rounds to N digits, HALF AWAY FROM ZERO (round(2.5) = 3,
	// round(-2.5) = -3, round(-50.55, -2) = -100, round(21.5, -1) = 20), and the precision may be
	// NEGATIVE. Neither native round() is faithful: SQLite's printf-based round disagrees on the negative
	// precision (round(21.5,-1) = 22 vs Kusto 20) and DuckDB's disagrees on both (it returned 0 for
	// round(-50.55,-2)); both were measured. So the shim is the definitional formula
	// sign(x) * floor(|x| * 10^p + 0.5) / 10^p, which measured IDENTICAL on both dialects for every
	// documented example. Bucket-free (no banker's rounding) by construction.
	// NB: the negation of the operand is spelled `(0 - {0})`, NOT `-{0}` — linq2db prints a negated constant
	// LITERAL with its sign, so `-{0}` would render `--50.55`, which every SQL dialect reads as a COMMENT.
	[Sql.Expression(
		"(CASE WHEN {0} < 0 THEN (0 - floor((0 - {0}) * pow(10, {1}) + 0.5)) / pow(10, {1}) ELSE floor({0} * pow(10, {1}) + 0.5) / pow(10, {1}) END)",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? RoundDouble(double? v, long precision)
	{
		if (v is not { } x)
			return null;
		var scale = Math.Pow(10, precision);
		return x < 0 ? -Math.Floor(-x * scale + 0.5) / scale : Math.Floor(x * scale + 0.5) / scale;
	}

	[Sql.Expression("(CASE WHEN {0} < 0 THEN NULL ELSE sqrt({0}) END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? Sqrt(double? v) => v is { } x && x >= 0 ? Math.Sqrt(x) : null;

	// pow: a negative base with a NON-INTEGRAL exponent is a domain error — SQLite yields NULL, DuckDB
	// yields NaN, and NaN is not representable in a SQLite REAL at all. Pin both to NULL.
	[Sql.Expression("(CASE WHEN {0} < 0 AND {1} <> CAST({1} AS BIGINT) THEN NULL ELSE pow({0}, {1}) END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? Pow(double? b, double? e) =>
		b is { } x && e is { } y && (x >= 0 || y == Math.Truncate(y)) ? Math.Pow(x, y) : null;

	[Sql.Expression("exp({0})", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? Exp(double? v) => v is { } x ? Math.Exp(x) : null;

	// log/log2/log10 are defined for x > 0 only (Kusto: log "is null if the argument is negative or null");
	// the guard also keeps DuckDB from RAISING on a non-positive input.
	[Sql.Expression("(CASE WHEN {0} > 0 THEN ln({0}) ELSE NULL END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? LogNatural(double? v) => v is { } x && x > 0 ? Math.Log(x) : null;

	[Sql.Expression("(CASE WHEN {0} > 0 THEN log2({0}) ELSE NULL END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? Log2(double? v) => v is { } x && x > 0 ? Math.Log2(x) : null;

	[Sql.Expression("(CASE WHEN {0} > 0 THEN log10({0}) ELSE NULL END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? Log10(double? v) => v is { } x && x > 0 ? Math.Log10(x) : null;

	// sign returns -1/0/1 as an INTEGER (DuckDB's sign returns a TINYINT, so the CAST pins the type).
	[Sql.Expression("CAST(sign({0}) AS BIGINT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? Sign(double? v) => v is { } x ? Math.Sign(x) : null;

	[Sql.Expression("pi()", ServerSideOnly = true)]
	public static double Pi() => Math.PI;

	// isnan: `x <> x` is TRUE only for NaN — DuckDB computes it natively; SQLite CANNOT STORE NaN (it
	// folds every NaN to NULL at the storage boundary), so FALSE is the truthful answer there, not a
	// divergence papered over. Null pins to FALSE rather than NULL (a total predicate, like isascii).
	[Sql.Expression("COALESCE({0} <> {0}, FALSE)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool IsNaNFn(double? v) => v is { } x && double.IsNaN(x);

	// isinf: DBL_MAX is finite in both dialects (measured), so `|x| > DBL_MAX` is true exactly for ±inf.
	[Sql.Expression("COALESCE(abs({0}) > 1.7976931348623157e308, FALSE)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static bool IsInfFn(double? v) => v is { } x && double.IsInfinity(x);

	// min_of/max_of/coalesce over STRINGS (the numeric arms are composed in the registry from HasValue
	// tests and comparisons, like NullGuard). Kusto: "Non-null values take precedence to null values" for
	// min/max, and for coalesce "the first arg whose value isn't null (or not-empty for string
	// expressions)" — so an EMPTY string is skipped, which plain COALESCE would not do.
	[Sql.Expression("(CASE WHEN {0} IS NULL THEN {1} WHEN {1} IS NULL THEN {0} WHEN {0} > {1} THEN {0} ELSE {1} END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? MaxOfString(string? a, string? b) =>
		a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;

	[Sql.Expression("(CASE WHEN {0} IS NULL THEN {1} WHEN {1} IS NULL THEN {0} WHEN {0} < {1} THEN {0} ELSE {1} END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? MinOfString(string? a, string? b) =>
		a is null ? b : b is null ? a : string.CompareOrdinal(a, b) <= 0 ? a : b;

	[Sql.Expression("(CASE WHEN {0} IS NULL OR {0} = '' THEN {1} ELSE {0} END)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? CoalesceString(string? a, string? b) => string.IsNullOrEmpty(a) ? b : a;

	// === datetime part extraction + construction, in the epoch-ms storage domain (a datetime is a long
	// here; the registry bridges to the logical DateTime/TimeSpan). Every extractor has a per-dialect arm:
	// SQLite spells them with strftime, DuckDB with the extract functions over epoch_ms(…). ---

	// dayofweek returns a TIMESPAN — "the number of days since the preceding Sunday" (kusto/query/
	// day-of-week-function), so the SQL answer is a day index in TICKS (the storage form of a duration).
	// Both dialects number Sunday = 0.
	[Sql.Expression("(CAST(strftime('%w', {0} / 1000, 'unixepoch') AS INTEGER) * 864000000000)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "(CAST(dayofweek(epoch_ms({0})) AS BIGINT) * 864000000000)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static long DayOfWeekTicks(long ms) => (long)FromUnixMs(ms).DayOfWeek * TimeSpan.TicksPerDay;

	[Sql.Expression("CAST(strftime('%d', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "CAST(day(epoch_ms({0})) AS BIGINT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static long DayOfMonthOfMs(long ms) => FromUnixMs(ms).Day;

	[Sql.Expression("CAST(strftime('%j', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "CAST(dayofyear(epoch_ms({0})) AS BIGINT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static long DayOfYearOfMs(long ms) => FromUnixMs(ms).DayOfYear;

	[Sql.Expression("CAST(strftime('%H', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "CAST(hour(epoch_ms({0})) AS BIGINT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static long HourOfMs(long ms) => FromUnixMs(ms).Hour;

	[Sql.Expression("CAST(strftime('%M', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "CAST(minute(epoch_ms({0})) AS BIGINT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static long MinuteOfMs(long ms) => FromUnixMs(ms).Minute;

	[Sql.Expression("CAST(strftime('%S', {0} / 1000, 'unixepoch') AS INTEGER)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "CAST(second(epoch_ms({0})) AS BIGINT)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static long SecondOfMs(long ms) => FromUnixMs(ms).Second;

	// quarter = (month - 1) / 3 + 1. DuckDB's `/` on integers is FLOAT division, so the integer division
	// has to be spelled `//` there (same reason as BinDateTimeMs).
	[Sql.Expression("((CAST(strftime('%m', {0} / 1000, 'unixepoch') AS INTEGER) - 1) / 3 + 1)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	[Sql.Expression(ProviderName.DuckDB, "(((CAST(month(epoch_ms({0})) AS BIGINT) - 1) // 3) + 1)", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static long QuarterOfMs(long ms) => (FromUnixMs(ms).Month - 1) / 3 + 1;

	// week_of_year is ISO 8601 ("the first week of a year is the one that includes the first Thursday",
	// kusto/query/week-of-year-function: 2020-12-31 → 53, 2000-01-01 → 52, 1970-01-01 → 1). DuckDB has it
	// natively (week() = ISO week); SQLite does not (strftime %W is Monday-based, 00-53, NOT ISO), so the
	// SQLite arm uses the standard Thursday trick: the ISO week of D is the week containing D's Thursday,
	// i.e. (dayofyear(Thursday) - 1) / 7 + 1 — Thursday = D + (4 - isoDow(D)) days, isoDow = ((dow+6)%7)+1.
	// Both arms are pinned against each other over year boundaries by KqlScalarFunctionsBatchTests.
	[Sql.Expression(
		"((CAST(strftime('%j', ({0} + (4 - (((CAST(strftime('%w', {0} / 1000, 'unixepoch') AS INTEGER) + 6) % 7) + 1)) * 86400000) / 1000, 'unixepoch') AS INTEGER) - 1) / 7 + 1)",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	[Sql.Expression(ProviderName.DuckDB, "week(epoch_ms({0}))", ServerSideOnly = true, IsNullable = Sql.IsNullableType.NotNullable)]
	public static long WeekOfYearOfMs(long ms)
	{
		var d = FromUnixMs(ms);
		var isoDow = ((int)d.DayOfWeek + 6) % 7 + 1;
		var thursday = d.AddDays(4 - isoDow);
		return (thursday.DayOfYear - 1) / 7 + 1;
	}

	// Ticks (the storage form of a duration) → the in-memory TimeSpan. Bridges make_timespan()/dayofweek()
	// into the row/logical-typing domain, where a duration column's LOGICAL type is TimeSpan. The nullable
	// form is what make_timespan() returns when a component is out of range.
	public static TimeSpan? FromTicksN(long? ticks) => ticks is { } t ? TimeSpan.FromTicks(t) : null;

	// make_timespan(day, hour, minute, second) — the registry passes 0 for the components the arity does
	// not carry. Kusto returns NULL when a component is out of range (hour 0-23, minute 0-59, second
	// 0-59.9999999), which the CASE reproduces on both dialects. The result is TICKS (the storage form of
	// a duration); a real `second` is scaled through the double domain and CAST back to the integer tick
	// count (BIGINT, matching .NET's long ticks).
	[Sql.Expression(
		"(CASE WHEN {1} < 0 OR {1} > 23 OR {2} < 0 OR {2} > 59 OR {3} < 0 OR {3} >= 60 THEN NULL"
		+ " ELSE CAST((CAST({0} AS DOUBLE) * 86400.0 + {1} * 3600.0 + {2} * 60.0 + {3}) * 10000000 AS BIGINT) END)",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static long? MakeTimespanTicks(long day, long hour, long minute, double second) =>
		hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or >= 60
			? null
			: (long)Math.Round((day * 86400.0 + hour * 3600.0 + minute * 60.0 + second) * TimeSpan.TicksPerSecond);

	// === tohex: the two's-complement hex rendering of an integer, LOWERCASE (kusto/query/tohex-function:
	// tohex(256) = '100', tohex(-256) = 'ffffffffffffff00', tohex(256, 8) = '00000100'). Non-negative values
	// print minimally or zero-padded to the compile-time minLength; NEGATIVE values print their full
	// two's-complement width (16 digits for a long, 8 for an int). printf's own %x prints a MINUS SIGN for a
	// negative argument, so the negative arm splits the value into two 32-bit halves and masks each: SQLite
	// has no unsigned cast at all, and DuckDB's `>>` is a LOGICAL shift (measured: -256 >> 32 gave 0 there),
	// so the high half goes through a FLOOR division, which is arithmetic on both. `format` is the
	// compile-time literal ('%08x' / '%x'), so the padding is exact on both sides and needs no dialect arm.
	[Sql.Expression(
		"(CASE WHEN {0} < 0 THEN printf('%08x%08x', CAST(floor({0} / 4294967296.0) AS BIGINT) & 4294967295, {0} & 4294967295) ELSE printf({1}, {0}) END)",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? ToHexLong(long? v, string format) =>
		v is { } x
			? (x < 0
				? unchecked((ulong)x).ToString("x16", CultureInfo.InvariantCulture)
				: HexNonNegative(x, format))
			: null;

	[Sql.Expression(
		"(CASE WHEN {0} < 0 THEN printf('%08x', {0} & 4294967295) ELSE printf({1}, {0}) END)",
		ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? ToHexInt(long? v, string format) =>
		v is { } x
			? (x < 0
				? unchecked((uint)x).ToString("x8", CultureInfo.InvariantCulture)
				: HexNonNegative(x, format))
			: null;

	// The non-negative arm of tohex, mirrored exactly: '%x' is the minimal lowercase form, '%0Nx' zero-pads
	// to N (never truncates).
	static string HexNonNegative(long value, string printfFormat) =>
		value.ToString(printfFormat.Length <= 2 ? "x" : "x" + printfFormat[1..^1], CultureInfo.InvariantCulture);

	// The unified metric Value: COALESCE(ValueDouble, ValueLong) as a nullable double, so a Gauge/Sum
	// point exposes ONE numeric column regardless of which arm (double / int64) carried the value. NULL
	// when neither arm is set (a histogram/summary point). CAST the long arm to DOUBLE so both arms share
	// the double result type on the SQL side, matching the double? the streamed row and C# body produce.
	// DOUBLE is portable: SQLite maps DOUBLE→REAL affinity (zero-diff), while on DuckDB REAL is float32 and
	// would lose precision — DOUBLE is float64 there, matching the CLR double.
	[Sql.Expression("COALESCE({0}, CAST({1} AS DOUBLE))", ServerSideOnly = true, IsNullable = Sql.IsNullableType.Nullable)]
	public static double? MetricValue(double? valueDouble, long? valueLong) =>
		valueDouble ?? (valueLong.HasValue ? valueLong.Value : (double?)null);
}

