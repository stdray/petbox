namespace PetBox.Tests.Kql;

public sealed class DualExecutorTests
{
	static readonly IReadOnlyList<TestEvent> Dataset =
	[
		TestEvent.FromName(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "hello world", "svc-a"),
		TestEvent.FromName(2, new DateTime(2026, 4, 19, 10, 1, 0, DateTimeKind.Utc), "Error", "boom", "svc-b"),
		TestEvent.FromName(3, new DateTime(2026, 4, 19, 10, 2, 0, DateTimeKind.Utc), "Warning", "meh", "svc-a"),
		TestEvent.FromName(4, new DateTime(2026, 4, 19, 10, 3, 0, DateTimeKind.Utc), "Error", "crash on Earth"),
		TestEvent.FromName(5, new DateTime(2026, 4, 19, 10, 4, 0, DateTimeKind.Utc), "Debug", "starting", "svc-c"),
		TestEvent.FromName(6, new DateTime(2026, 4, 19, 10, 5, 0, DateTimeKind.Utc), "Information", "BOOM normalized", "svc-b"),
	];

	[Theory]
	[InlineData("events | where Level == 4")]
	[InlineData("events | where Level != 4")]
	[InlineData("events | where Level >= 3")]
	[InlineData("events | where Level > 3")]
	[InlineData("events | where Level <= 2")]
	[InlineData("events | where Level < 2")]
	[InlineData("events | where LevelName == 'Error'")]
	[InlineData("events | where LevelName != 'Information'")]
	[InlineData("events | where Message == 'boom'")]
	[InlineData("events | where Message != 'boom'")]
	[InlineData("events | where Id == 3")]
	[InlineData("events | where Id != 3")]
	[InlineData("events | where Id > 3")]
	[InlineData("events | where Id >= 3")]
	[InlineData("events | where Id < 3")]
	[InlineData("events | where Id <= 3")]
	[InlineData("events | where ServiceKey == 'svc-a'")]
	public async Task ScalarComparisons_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	// An INTEGER column against a REAL literal is a plain numeric comparison in Kusto (both sides promote
	// to double) — `Id > 0.5` keeps every row, `Id == 1.0` matches the integral 1. The column-literal fast
	// path cannot express the mixed-type compare and yields to the general path; the oracle pins that the
	// result is the numeric one and not an error. Reversed operand order too (`0.5 < Id`).
	[Theory]
	[InlineData("events | where Id > 0.5")]
	[InlineData("events | where Id >= 0.5")]
	[InlineData("events | where Id < 3.5")]
	[InlineData("events | where Id <= 3.5")]
	[InlineData("events | where Id == 1.0")]
	[InlineData("events | where Id != 1.0")]
	[InlineData("events | where Id == 1.5")]
	[InlineData("events | where Id > -0.5")]
	[InlineData("events | where 0.5 < Id")]
	[InlineData("events | where 3.5 >= Id")]
	[InlineData("events | where Level > 2.5")]
	public async Task IntColumnAgainstRealLiteral_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Message contains 'boom'")]
	[InlineData("events | where Message contains 'BOOM'")]
	[InlineData("events | where Message contains 'earth'")]
	[InlineData("events | where Message contains 'no-such-thing'")]
	public async Task Contains_CaseInsensitive_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	// startswith / endswith (+ _cs). Case-insensitive by default; _cs is case-sensitive.
	[Theory]
	[InlineData("events | where Message startswith 'crash'")]
	[InlineData("events | where Message startswith 'BOOM'")]     // ci: matches 'boom' and 'BOOM normalized'
	[InlineData("events | where Message startswith 'zzz'")]
	[InlineData("events | where Message startswith_cs 'BOOM'")]  // cs: only 'BOOM normalized'
	[InlineData("events | where Message startswith_cs 'boom'")]
	[InlineData("events | where Message endswith 'world'")]
	[InlineData("events | where Message endswith 'ng'")]
	[InlineData("events | where Message endswith 'EARTH'")]      // ci
	[InlineData("events | where Message endswith_cs 'Earth'")]
	[InlineData("events | where Message endswith_cs 'earth'")]
	public async Task StartsEndsWith_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	// `has` = whole-term match. These needles ARE whole terms in the data, so honest term-matching
	// and KustoLoco (which models `has` as a substring — see KqlStringOpsTests.Has_IsTermMatch_NotSubstring)
	// coincide here. The term-vs-substring DISTINCTION (e.g. `has 'art'` in "starting") is where the
	// two diverge and is pinned by production-only unit tests instead.
	[Theory]
	[InlineData("events | where Message has 'boom'")]
	[InlineData("events | where Message has 'BOOM'")]
	[InlineData("events | where Message has 'earth'")]
	[InlineData("events | where Message has 'crash'")]
	[InlineData("events | where Message has 'normalized'")]
	[InlineData("events | where Message has_cs 'BOOM'")]
	[InlineData("events | where Message has_cs 'boom'")]
	public async Task HasTerm_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Message matches regex '^b.*m$'")]
	[InlineData("events | where Message matches regex 'o.m'")]
	[InlineData("events | where Message matches regex '[0-9]'")]
	[InlineData("events | where Message matches regex 'wor.d'")]
	public async Task MatchesRegex_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | project Id, L = tolower(Message)")]
	[InlineData("events | project Id, U = toupper(Message)")]
	[InlineData("events | project Id, S = substring(Message, 0, 4)")]
	[InlineData("events | project Id, S = substring(Message, 2)")]
	[InlineData("events | project Id, C = strcat(Message, '!')")]
	[InlineData("events | project Id, C = strcat('[', Message, ']')")]
	[InlineData("events | project Id, W = extract('([A-Za-z]+)', 1, Message)")]
	public async Task StringFunctions_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Level >= 4 and ServiceKey == 'svc-b'")]
	[InlineData("events | where Level == 4 or Level == 3")]
	[InlineData("events | where Id > 1 and Id <= 4")]
	[InlineData("events | where not(Level == 4)")]
	[InlineData("events | where LevelName == 'Information' and Message contains 'hello'")]
	public async Task LogicalCombinators_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Fact]
	public async Task EmptyResult_BothSidesEmpty()
	{
		await DualExecutor.AssertSameAsync("events | where Level == 5", Dataset);
	}

	[Theory]
	[InlineData("events | order by Id")]
	[InlineData("events | order by Id asc")]
	[InlineData("events | order by Id desc")]
	[InlineData("events | order by Level asc, Id desc")]
	[InlineData("events | where Level >= 3 | order by Id")]
	public async Task OrderBy_PreservesOrdering(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset, ordered: true);
	}

	// NOTE: `in` / `between` / unary-minus are exercised over the long `Id` column here.
	// The reference engine (KustoLoco) throws an int→long column-cast error when these
	// specific operators run over the int `Level` column; that is a KustoLoco limitation,
	// not ours — production support for Level in/between is covered in KqlTransformerTests.
	[Theory]
	[InlineData("events | where Id in (2, 3, 4)")]
	[InlineData("events | where Id in (4)")]
	[InlineData("events | where Id !in (2, 3)")]
	[InlineData("events | where Id !in (2, 3, 4)")]
	[InlineData("events | where ServiceKey in ('svc-a', 'svc-c')")]
	[InlineData("events | where ServiceKey !in ('svc-a')")]
	public async Task InOperator_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Id between (2 .. 4)")]
	[InlineData("events | where Id between (4 .. 4)")]
	[InlineData("events | where Id !between (2 .. 3)")]
	public async Task BetweenOperator_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Level + 1 == 5")]
	[InlineData("events | where Level * 2 >= 8")]
	[InlineData("events | where Level - 1 < 2")]
	[InlineData("events | where Id % 2 == 0")]
	[InlineData("events | where Id / 2 == 2")]
	[InlineData("events | where -Id < -3")]
	[InlineData("events | where (Level + 1) * 2 == 10")]
	public async Task Arithmetic_InWhere_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where iff(Level == 4, 1, 0) == 1")]
	[InlineData("events | where iff(Level >= 3, Level, 0) >= 3")]
	[InlineData("events | where case(Level == 4, 'hi', Level == 3, 'mid', 'lo') == 'hi'")]
	[InlineData("events | where case(Level >= 4, 2, Level == 3, 1, 0) >= 1")]
	public async Task IffCase_InWhere_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | project Id, Doubled = Level * 2")]
	[InlineData("events | project Id, Plus = Level + 10")]
	[InlineData("events | project Id, Mod = Id % 2")]
	[InlineData("events | project Id, Half = Id / 2")]
	[InlineData("events | project Id, Combo = (Level + 1) * 3")]
	public async Task ComputedProject_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | extend Doubled = Level * 2 | project Id, Doubled")]
	[InlineData("events | extend Plus = Level + 10 | project Id, Plus")]
	[InlineData("events | extend Mod = Id % 2 | project Id, Mod")]
	[InlineData("events | extend Level = Level + 100 | project Id, Level")]
	[InlineData("events | where Level >= 3 | extend D = Level * 2 | project Id, D")]
	public async Task ComputedExtend_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | project Id, Sev = iff(Level >= 4, 'high', 'low')")]
	[InlineData("events | project Id, Bucket = case(Level >= 4, 'err', Level == 3, 'warn', 'info')")]
	[InlineData("events | extend Flag = iff(Id in (2, 4), 1, 0) | project Id, Flag")]
	[InlineData("events | extend Ok = iff(Id between (2 .. 3), 1, 0) | project Id, Ok")]
	public async Task ComputedConditionals_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, Dataset);
	}

	// A group dataset with every ServiceKey populated (so string-column results have no
	// null-vs-"" divergence) and timestamps spread across three hours for bin() bucketing.
	// Aggregate arguments run over the long Id column: KustoLoco throws an int->long column
	// cast for aggregates over the int Level column, so Level is covered by production-only
	// unit tests instead (see KqlResultTests).
	static readonly IReadOnlyList<TestEvent> GroupData =
	[
		TestEvent.FromName(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "a", "svc-a"),
		TestEvent.FromName(2, new DateTime(2026, 4, 19, 10, 20, 0, DateTimeKind.Utc), "Error", "b", "svc-a"),
		TestEvent.FromName(3, new DateTime(2026, 4, 19, 11, 0, 0, DateTimeKind.Utc), "Warning", "c", "svc-b"),
		TestEvent.FromName(4, new DateTime(2026, 4, 19, 11, 30, 0, DateTimeKind.Utc), "Error", "d", "svc-b"),
		TestEvent.FromName(5, new DateTime(2026, 4, 19, 12, 5, 0, DateTimeKind.Utc), "Error", "e", "svc-a"),
		TestEvent.FromName(6, new DateTime(2026, 4, 19, 12, 5, 0, DateTimeKind.Utc), "Information", "f", "svc-c"),
	];

	[Theory]
	[InlineData("events | summarize Total = count() by ServiceKey")]
	[InlineData("events | summarize S = sum(Id) by ServiceKey")]
	[InlineData("events | summarize Mn = min(Id) by ServiceKey")]
	[InlineData("events | summarize Mx = max(Id) by ServiceKey")]
	[InlineData("events | summarize A = avg(Id) by ServiceKey")]
	[InlineData("events | summarize D = dcount(ServiceKey)")]
	[InlineData("events | summarize D = dcount(ServiceKey) by ServiceKey")]
	[InlineData("events | summarize C = countif(Level >= 4) by ServiceKey")]
	[InlineData("events | summarize Total = count(), S = sum(Id), Mn = min(Id), Mx = max(Id) by ServiceKey")]
	public async Task SummarizeAggregates_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	// make_list / make_set: production yields COMPACT value-ascending JSON; KustoLoco yields a JsonArray in
	// encounter order. DualExecutor.Norm reduces every JSON-array cell to a SORTED MULTISET, so these assert
	// the SAME ELEMENTS (null-skipped; deduped for make_set) regardless of order/representation. Selectors
	// are string (ServiceKey/Message) and long (Id) — the v1 supported set; ServiceKey has duplicates so
	// make_set genuinely dedups where make_list does not.
	[Theory]
	[InlineData("events | summarize L = make_list(ServiceKey)")]
	[InlineData("events | summarize S = make_set(ServiceKey)")]
	[InlineData("events | summarize L = make_list(Message) by ServiceKey")]
	[InlineData("events | summarize L = make_list(Id) by ServiceKey")]
	[InlineData("events | summarize S = make_set(Id) by ServiceKey")]
	[InlineData("events | summarize S = make_set(ServiceKey) by Level")]
	public async Task SummarizeMakeListSet_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	// A NUMERIC bin() as a summarize group KEY (`by Bucket = bin(Id, N)`) now translates to SQL: bin's
	// numeric path routes through KqlSqlExpressions.BinLong/BinDouble ([Sql.Expression] arithmetic) instead
	// of the old in-memory-only helper, so the GROUP BY key is server-translatable (was untranslatable —
	// 'x.Key' could not be converted — and masked by EnumerableQuery until the harness cutover).
	[Theory]
	[InlineData("events | summarize Cnt = count() by Bucket = bin(Id, 2)")]
	[InlineData("events | summarize Cnt = count() by Bucket = bin(Id, 3)")]
	[InlineData("events | summarize Sm = sum(Id) by Bucket = bin(Id, 2)")]
	public async Task SummarizeByNumericBin_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	[Theory]
	[InlineData("events | summarize Cnt = count() by Hour = bin(Timestamp, 1h)")]
	[InlineData("events | summarize Cnt = count() by Bucket = bin(Timestamp, 5m)")]
	[InlineData("events | summarize Cnt = count() by Bucket = bin(Timestamp, 1d)")]
	public async Task SummarizeByTimeBin_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	// bin(datetime, timespan) in a project/extend runs through the SQL translation (BinDateTimeMs, epoch-ms
	// bucketing) — distinct from the summarize-by-bin cases above (in-memory). Pins the bucketed DateTime
	// value AND logical ClrType byte-identical to the reference engine.
	[Theory]
	[InlineData("events | project Id, Hour = bin(Timestamp, 1h)")]
	[InlineData("events | extend Bucket = bin(Timestamp, 5m) | project Id, Bucket")]
	[InlineData("events | project Id, Day = bin(Timestamp, 1d)")]
	public async Task ProjectExtendTimeBin_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	[Theory]
	[InlineData("events | project Id, Lvl = Level | order by Id desc")]
	[InlineData("events | project Id, Lvl = Level | order by Lvl asc, Id desc")]
	[InlineData("events | extend D = Id * 2 | project Id, D | order by D asc")]
	[InlineData("events | summarize Total = count() by ServiceKey | order by Total desc, ServiceKey asc")]
	public async Task PostShapeOrderBy_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData, ordered: true);
	}

	[Theory]
	[InlineData("events | project Id, Lvl = Level | order by Id desc | take 2")]
	[InlineData("events | project Id, Lvl = Level | order by Id asc | take 3")]
	public async Task PostShapeOrderTake_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData, ordered: true);
	}

	[Theory]
	[InlineData("events | project Id, Lvl = Level | top 3 by Id desc")]
	[InlineData("events | project Id, Lvl = Level | top 2 by Id asc")]
	[InlineData("events | extend D = Id * 2 | project Id, D | top 3 by D desc")]
	public async Task PostShapeTop_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData, ordered: true);
	}

	[Theory]
	[InlineData("events | distinct ServiceKey")]
	[InlineData("events | distinct ServiceKey, Level")]
	[InlineData("events | where Level >= 4 | distinct ServiceKey")]
	public async Task Distinct_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	// datetime functions over columns/literals (NOT now()/ago(), whose value KustoLoco owns — those
	// are pinned-clock production-only unit tests in KqlDateTimeTests).
	[Theory]
	[InlineData("events | project Id, D = startofday(Timestamp)")]
	[InlineData("events | project Id, D = startofweek(Timestamp)")]
	[InlineData("events | project Id, D = startofmonth(Timestamp)")]
	[InlineData("events | project Id, D = startofyear(Timestamp)")]
	[InlineData("events | extend D = startofday(Timestamp) | project Id, D")]
	public async Task StartOf_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	// datetime_diff is NOT differential-tested: KustoLoco tz-shifts datetime() literals by the host's
	// local offset and uses raw (not period-boundary) truncation, so it disagrees with real Kusto on
	// non-UTC hosts. Our boundary semantics are pinned against the canonical Kusto doc examples in
	// KqlDateTimeTests instead.
	[Theory]
	[InlineData("events | summarize Cnt = count() by Day = startofday(Timestamp)")]
	[InlineData("events | summarize Cnt = count() by Month = startofmonth(Timestamp)")]
	public async Task SummarizeByStartOf_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	// where AFTER a shape-changing op filters the computed rows in memory (the headline case:
	// filter groups by an aggregate).
	[Theory]
	[InlineData("events | summarize Total = count() by ServiceKey | where Total >= 2")]
	[InlineData("events | summarize Total = count() by ServiceKey | where Total > 2")]
	[InlineData("events | summarize S = sum(Id) by ServiceKey | where S > 5")]
	[InlineData("events | project Id, Lvl = Level | where Lvl >= 4")]
	[InlineData("events | extend D = Id * 2 | where D > 6 | project Id, D")]
	[InlineData("events | summarize Total = count() by ServiceKey | where Total >= 2 and ServiceKey != 'svc-b'")]
	public async Task PostShapeWhere_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData);
	}

	[Theory]
	[InlineData("events | summarize Total = count() by ServiceKey | where Total >= 2 | order by Total desc, ServiceKey asc")]
	[InlineData("events | extend D = Id * 2 | where D > 4 | project Id, D | order by D asc")]
	public async Task PostShapeWhere_ThenOrder_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, GroupData, ordered: true);
	}

	// Real per-row properties, carried into BOTH engines (production: PropertiesJson; KustoLoco: a
	// dynamic Properties column). Values are stored with native JSON types (number / real / string).
	// The bare-name fallback is NOT here — KustoLoco has no such fallback — it is production-only
	// (KqlTypedPropertiesTests). todatetime/tobool are also production-only (KustoLoco tz-shifts and
	// models dynamic differently); toint/tolong/todouble/tostring over Properties cooperate.
	static readonly IReadOnlyList<TestEvent> PropsData =
	[
		TestEvent.FromName(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "a", "svc-a",
			new Dictionary<string, object?> { ["Status"] = 200, ["Ratio"] = 1.5, ["user"] = "alice" }),
		TestEvent.FromName(2, new DateTime(2026, 4, 19, 10, 1, 0, DateTimeKind.Utc), "Error", "b", "svc-b",
			new Dictionary<string, object?> { ["Status"] = 500, ["Ratio"] = 2.5, ["user"] = "bob" }),
		TestEvent.FromName(3, new DateTime(2026, 4, 19, 10, 2, 0, DateTimeKind.Utc), "Warning", "c", "svc-a",
			new Dictionary<string, object?> { ["Status"] = 404, ["Ratio"] = 0.25, ["user"] = "carol" }),
	];

	[Theory]
	[InlineData("events | where toint(Properties.Status) >= 400")]
	[InlineData("events | where toint(Properties.Status) < 300")]
	[InlineData("events | where tolong(Properties.Status) == 500")]
	[InlineData("events | where todouble(Properties.Ratio) > 1.0")]
	public async Task TypedPropertyFilters_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, PropsData);
	}

	[Theory]
	[InlineData("events | project Id, S = toint(Properties.Status)")]
	[InlineData("events | project Id, R = todouble(Properties.Ratio)")]
	[InlineData("events | project Id, U = tostring(Properties.user)")]
	public async Task TypedPropertyProjections_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, PropsData);
	}

	[Theory]
	[InlineData("events | summarize C = count() by U = tostring(Properties.user)")]
	[InlineData("events | summarize Total = sum(toint(Properties.Status))")]
	public async Task TypedPropertyAggregates_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, PropsData);
	}

	// NULLABLE numeric contexts (kql-nullable-numeric-contexts): production reads EVERY bag value as text, so
	// numeric analysis of a property runs through toint/todouble → Nullable<T>. bin(), arithmetic and unary
	// minus must lift over that nullable and PROPAGATE the null (null bucket / null cell) exactly like the
	// reference engine — never fail the query, never coalesce to 0.
	//
	// The null here comes from a MISSING key (row 4), not an unparseable string: KustoLoco's toint over a
	// dynamic STRING yields null even for "1200" (it models dynamic differently — the same limitation that
	// keeps todatetime/tobool out of the differential), so a string-valued bag can't be an oracle case. The
	// unparseable-string row and the string-typed bag are pinned production-side (KqlTypedPropertiesTests
	// .Bin_OverConvertedProperty_* / Arithmetic_OverConvertedProperty_*), which is where the real ingest shape
	// lives anyway.
	static readonly IReadOnlyList<TestEvent> NullableNumericData =
	[
		TestEvent.FromName(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "a", "svc-a",
			new Dictionary<string, object?> { ["RespChars"] = 1200 }),
		TestEvent.FromName(2, new DateTime(2026, 4, 19, 10, 1, 0, DateTimeKind.Utc), "Information", "b", "svc-a",
			new Dictionary<string, object?> { ["RespChars"] = 6000 }),
		TestEvent.FromName(3, new DateTime(2026, 4, 19, 10, 2, 0, DateTimeKind.Utc), "Information", "c", "svc-b",
			new Dictionary<string, object?> { ["RespChars"] = 4999 }),
		TestEvent.FromName(4, new DateTime(2026, 4, 19, 10, 3, 0, DateTimeKind.Utc), "Information", "d", "svc-b",
			new Dictionary<string, object?> { ["Other"] = 1 }), // no RespChars → toint(...) is null
	];

	[Theory]
	[InlineData("events | extend rc = toint(Properties.RespChars) | summarize C = count() by Bucket = bin(rc, 5000)")]
	[InlineData("events | summarize C = count() by Bucket = bin(toint(Properties.RespChars), 5000)")]
	// NOT here: `summarize sum(toint(Properties.X)) by bin(…)` — the REFERENCE engine throws its known
	// int→long column-cast on an aggregate over a converted column (same KustoLoco limitation the Level
	// aggregates hit above), so the sum-per-bucket case is pinned production-side instead
	// (KqlTypedPropertiesTests.Aggregates_OverConvertedProperty_PerNullableBin).
	public async Task NullableNumericBin_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, NullableNumericData);
	}

	[Theory]
	[InlineData("events | extend chars = toint(Properties.RespChars) | extend sharePct = 100.0 * chars / 12000 | project Id, sharePct")]
	[InlineData("events | project Id, X = toint(Properties.RespChars) + 1")]
	[InlineData("events | project Id, X = todouble(Properties.RespChars) * 2.0")]
	// NOT here: unary minus over a converted column — the reference engine throws the SAME int→long
	// column-cast it throws for `-Level` (see the in/between/unary-minus note above). Pinned production-side
	// (KqlTypedPropertiesTests.Arithmetic_OverConvertedProperty_SharePct_NullsPropagate, column `Neg`).
	public async Task NullableNumericArithmetic_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, NullableNumericData);
	}

	// REAL-domain (kql-double-literal-real-domain): a double literal whose value is INTEGRAL (5000.0, 2.0) is
	// the trap — linq2db prints it as the bare integer `5000` and elides the Convert, so a dynamically-typed
	// backend would do INTEGER math and truncate. The reference engine is the arbiter of what these must
	// return: bin()'s double arm stays real (`bin(Id, 2.0)` is 2.0, not 2), and an iff/case result divided by
	// a double literal keeps its fraction. `Id / 2` (two integers) stays INTEGER division on BOTH engines —
	// the Kusto semantics the guard must not break.
	[Theory]
	[InlineData("events | project Id, X = bin(toint(Properties.RespChars), 5000.0)")]
	[InlineData("events | project Id, X = bin(Id, 2.0), Y = bin(Id, 2.0) / 4")]
	[InlineData("events | summarize C = count() by Bucket = bin(Id, 2.0)")]
	[InlineData("events | project Id, X = iff(Id > 2, 3, 1) / 2.0, Y = case(Id > 2, 3, Id > 0, 1, 0) / 2.0")]
	[InlineData("events | project Id, IntDiv = Id / 2, RealDiv = Id / 2.0")]
	public async Task DoubleLiteralRealDomain_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, NullableNumericData);
	}

	// --- correlation: join / lookup over a same-log subquery. The right subquery is a full pipeline
	// over `events`. Both engines fully collide the self-join column names, so the right Id becomes
	// `Id1`; a trailing `project Id, Id1` aligns the two engines' differing event shapes to a common
	// pair of columns. Row order is not asserted (join output order is unspecified). mv-expand and
	// parse are NOT here — the reference executor can't expand our Properties-JSON strings and doesn't
	// implement parse — they are pinned production-side in KqlCorrelationTests.
	static readonly IReadOnlyList<TestEvent> JoinData =
	[
		TestEvent.FromName(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "a", "svc-a"),
		TestEvent.FromName(2, new DateTime(2026, 4, 19, 10, 1, 0, DateTimeKind.Utc), "Error", "b", "svc-a"),
		TestEvent.FromName(3, new DateTime(2026, 4, 19, 10, 2, 0, DateTimeKind.Utc), "Warning", "c", "svc-b"),
		TestEvent.FromName(4, new DateTime(2026, 4, 19, 10, 3, 0, DateTimeKind.Utc), "Error", "d", "svc-b"),
		TestEvent.FromName(5, new DateTime(2026, 4, 19, 10, 4, 0, DateTimeKind.Utc), "Error", "e", "svc-c"),
	];

	[Theory]
	// inner: every left×right match on the key
	[InlineData("events | where Level == 4 | join kind=inner (events | where Level >= 3) on ServiceKey | project Id, Id1")]
	// leftouter: unmatched left rows survive with a null right Id
	[InlineData("events | join kind=leftouter (events | where Level == 4) on ServiceKey | project Id, Id1")]
	// multi-key equijoin
	[InlineData("events | join kind=inner (events) on ServiceKey, Level | project Id, Id1")]
	// explicit $left/$right equality (inner)
	[InlineData("events | join kind=inner (events | where Level >= 3) on $left.ServiceKey == $right.ServiceKey | project Id, Id1")]
	public async Task Join_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, JoinData);
	}

	// innerunique de-dups the LEFT side by key keeping the FIRST left row per key (Kusto table order).
	// ComposeJoin's DedupByKey now tie-breaks ascending by the left identity column (= insertion order),
	// so linq2db's ROW_NUMBER dedup and KustoLoco agree byte-identically over real SQLite (was arbitrary
	// before the tie-break — the harness cutover surfaced it). Only the EXPLICIT kind=innerunique form is
	// differential: a bare join now defaults to inner here (spec kql-join-default-inner) while KustoLoco
	// still defaults to innerunique, so a kind-less query cannot be compared from the same string.
	[Theory]
	[InlineData("events | join kind=innerunique (events | where Level >= 3) on ServiceKey | project Id, Id1")]
	public async Task Join_InnerUnique_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, JoinData);
	}

	[Theory]
	// lookup = leftouter, first matching right row only, right key column dropped from the output
	[InlineData("events | lookup (events | where Level == 4) on ServiceKey | project Id, Id1")]
	[InlineData("events | lookup (events) on ServiceKey | project Id, Message1")]
	public async Task Lookup_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, JoinData);
	}

	// F5 (review) — sequential extend/project column refs (`extend A = …, B = f(A)`) are pinned
	// PRODUCTION-ONLY in KqlReviewFixesTests: the reference executor (like real Kusto) REJECTS a
	// reference to a column introduced earlier in the same operator, so it cannot be differential.

	// F9 (review): a no-by summarize over EMPTY input returns ONE row of default aggregates, not zero.
	[Fact]
	public async Task SummarizeNoBy_OverEmptyInput_YieldsSingleDefaultRow()
	{
		await DualExecutor.AssertSameTableAsync("events | where Level == 99 | summarize C = count()", Dataset);
	}
}
