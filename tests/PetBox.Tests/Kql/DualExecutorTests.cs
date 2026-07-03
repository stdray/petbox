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
	// default kind = innerunique: the left side is de-duplicated by key (first row per key)
	[InlineData("events | join (events | where Level == 4) on ServiceKey | project Id, Id1")]
	[InlineData("events | join kind=innerunique (events | where Level >= 3) on ServiceKey | project Id, Id1")]
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
