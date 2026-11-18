using Kusto.Language;

namespace PetBox.Tests.Kql;

// Production-side pins for `summarize`'s arg_max/arg_min row pick and the filtered (`*if`) aggregate family —
// the aggregates KustoLoco cannot referee (it has no arg_max row-pick contract and no *if family), so
// correctness is pinned HERE by (a) SQLite == DuckDB value-exact equality on every query and (b)
// hand-computed expectations.
//
// TIE-BREAK (the one semantic SQL leaves undefined): arg_max/arg_min resolve ties by the row that comes
// FIRST in input order — for `events` that is the lowest Id, which is this project's monotonic ingestion
// identity (an [Identity] primary key). The window pre-stage spells it out as "every projected column
// ascending, nulls last" precisely so the answer never depends on a backend's sorter, and the tests below
// seed the rows in REVERSE Id order to prove the pick is Id-driven, not disk/scan order.
//
// NULL semantics: a null in the maximized expression always loses to a non-null one, and a group whose
// values are ALL null still returns a row (with null values) — Kusto's documented behaviour, not SQL's.
// An all-false `*if` group is null for avgif/minif/maxif, 0 for dcountif/count_distinctif, and "[]" for
// make_list_if/make_set_if.
public sealed class KqlArgMaxAndFilteredAggregateTests
{
	static LogEntryRecord Rec(long id, int level = 2, string message = "m", string serviceKey = "s", string? exception = null,
		string props = "{}") => new()
		{
			Id = id,
			Level = level,
			Message = message,
			MessageTemplate = message,
			ServiceKey = serviceKey,
			Exception = exception,
			PropertiesJson = props,
			TimestampMs = id,
		};

	// Runs the query on BOTH backends, asserts value-exact equality (the cross-backend pin), returns the
	// SQLite rows. Grouped queries must carry their own `order by` — row order is not a contract otherwise.
	static async Task<List<object?[]>> RunBoth(IReadOnlyList<LogEntryRecord> data, string kql)
	{
		var code = KustoCode.Parse(kql);
		var (_, sqlite) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.Sqlite);
		var (_, duck) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.DuckDb);
		duck.Should().BeEquivalentTo(sqlite, o => o.WithStrictOrdering(),
			$"both backends must agree value-exactly on {kql}");
		return sqlite;
	}

	// Same, also returning the column names (arg_max/arg_min name one output column per argument).
	static async Task<(IReadOnlyList<string> Columns, List<object?[]> Rows)> RunBothNamed(
		IReadOnlyList<LogEntryRecord> data, string kql)
	{
		var code = KustoCode.Parse(kql);
		var (sqliteCols, sqlite) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.Sqlite);
		var (duckCols, duck) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.DuckDb);
		duck.Should().BeEquivalentTo(sqlite, o => o.WithStrictOrdering(),
			$"both backends must agree value-exactly on {kql}");
		duckCols.Select(c => c.Name).Should().Equal(sqliteCols.Select(c => c.Name),
			"the output schema is backend-independent");
		return (sqliteCols.Select(c => c.Name).ToList(), sqlite);
	}

	// --- arg_max / arg_min ---

	// The row that maximizes Level per group, its Message returned. Multi-column: EVERY returned expression
	// comes from that same row (Message and the Id-via-Timestamp of one row, never a mix).
	[Fact]
	public async Task ArgMax_PicksTheRow_PerGroup()
	{
		var data = new[]
		{
			Rec(1, 1, "a-low", "a"),
			Rec(2, 9, "a-high", "a"),
			Rec(3, 5, "a-mid", "a"),
			Rec(4, 7, "b-high", "b"),
			Rec(5, 2, "b-low", "b"),
		};
		var (cols, rows) = await RunBothNamed(data,
			"events | summarize arg_max(Level, Message, Timestamp) by ServiceKey | order by ServiceKey asc");

		cols.Should().Equal("ServiceKey", "Level", "Message", "Timestamp");
		rows.Should().HaveCount(2);
		rows[0].Should().Equal("a", 9, "a-high", DateTimeOffset.FromUnixTimeMilliseconds(2).UtcDateTime);
		rows[1].Should().Equal("b", 7, "b-high", DateTimeOffset.FromUnixTimeMilliseconds(4).UtcDateTime);
	}

	// arg_min is the exact mirror (Level minimized, row values returned).
	[Fact]
	public async Task ArgMin_PicksTheRow_PerGroup()
	{
		var data = new[]
		{
			Rec(1, 4, "a-4", "a"),
			Rec(2, 1, "a-1", "a"),
			Rec(3, 8, "b-8", "b"),
			Rec(4, 6, "b-6", "b"),
		};
		var rows = await RunBoth(data,
			"events | summarize arg_min(Level, Message) by ServiceKey | order by ServiceKey asc");

		rows.Should().HaveCount(2);
		rows[0].Should().Equal("a", 1, "a-1");
		rows[1].Should().Equal("b", 6, "b-6");
	}

	// TIE-BREAK, the pinned contract: equal maximized values resolve to the FIRST row in input order — the
	// lowest Id. The rows are seeded in REVERSE Id order (id 2 lands on disk before id 1), so a scan-order
	// answer would be wrong; both output columns must come from the id-1 row.
	[Fact]
	public async Task ArgMax_TiesResolveToTheFirstRowInInputOrder()
	{
		var data = new[]
		{
			Rec(3, 5, "third"),
			Rec(2, 9, "second"),
			Rec(1, 9, "first"),
			Rec(4, 9, "fourth"),
		};
		var rows = await RunBoth(data, "events | summarize arg_max(Level, Message, Id)");

		rows.Should().HaveCount(1);
		rows[0].Should().Equal(9, "first", 1L);
	}

	// arg_min ties resolve the same way (mirror of the pin above).
	[Fact]
	public async Task ArgMin_TiesResolveToTheFirstRowInInputOrder()
	{
		var data = new[]
		{
			Rec(3, 5, "third"),
			Rec(2, 1, "second"),
			Rec(1, 1, "first"),
		};
		var rows = await RunBoth(data, "events | summarize arg_min(Level, Message)");

		rows.Should().HaveCount(1);
		rows[0].Should().Equal(1, "first");
	}

	// Nulls in the maximized expression LOSE to any non-null value, and a group whose values are all null
	// still returns its row (Kusto: null Version still yields the row's other columns — never no row).
	[Fact]
	public async Task ArgMax_NullMaximized_LosesToNonNull_ButAllNullGroupStillReturnsARow()
	{
		var data = new[]
		{
			Rec(1, message: "a-null", serviceKey: "a"),
			Rec(2, 3, "a-3", "a"),
			Rec(3, message: "b-null-1", serviceKey: "b"),
			Rec(4, message: "b-null-2", serviceKey: "b"),
		};
		// tolong(Properties.v) is null for every seeded row, so BOTH groups maximize over nothing — and each
		// still returns its first-in-input-order row (Kusto's null-arg example yields the row with null values).
		var rows = await RunBoth(data,
			"events | extend V = tolong(Properties.v) " +
			"| summarize arg_max(V, Message) by ServiceKey | order by ServiceKey asc");

		rows.Should().HaveCount(2);
		rows[0].Should().HaveCount(3);
		rows[0][0].Should().Be("a");
		rows[0][1].Should().BeNull();
		rows[0][2].Should().Be("a-null");
		rows[1].Should().HaveCount(3);
		rows[1][0].Should().Be("b");
		rows[1][1].Should().BeNull();
		rows[1][2].Should().Be("b-null-1");
	}

	// A null-maximizing row never beats a non-null one inside the same group.
	[Fact]
	public async Task ArgMax_NullNeverBeatsNonNull()
	{
		var data = new[]
		{
			Rec(1, message: "null-v", serviceKey: "a"),
			Rec(2, message: "has-v", serviceKey: "a", props: """{"v":"7"}"""),
		};
		var rows = await RunBoth(data,
			"events | extend V = tolong(Properties.v) " +
			"| summarize arg_max(V, Message) by ServiceKey");

		rows.Should().HaveCount(1);
		rows[0].Should().Equal("a", 7L, "has-v");
	}

	// Empty input, no-`by`: Kusto's one-default-row rule — a single row of nulls (arg_max has nothing to
	// pick; the value columns are null, NOT an empty result).
	[Fact]
	public async Task ArgMax_EmptyInput_NoBy_YieldsOneRowOfNulls()
	{
		var data = new[] { Rec(1, 3, "x") };
		var rows = await RunBoth(data, "events | where Level > 99 | summarize arg_max(Level, Message)");

		rows.Should().HaveCount(1);
		rows[0].Should().HaveCount(2);
		rows[0][0].Should().BeNull();
		rows[0][1].Should().BeNull();
	}

	// The `summarize ... by` interaction: the window partition and the GROUP BY are the same key sets, and
	// arg_max composes with plain aggregates over the same pre-staged rows.
	[Fact]
	public async Task ArgMax_GroupsIndependently_AndMixesWithOtherAggregates()
	{
		var data = new[]
		{
			Rec(1, 1, "a1", "a"),
			Rec(2, 5, "a5", "a"),
			Rec(3, 3, "a3", "a"),
			Rec(4, 2, "b2", "b"),
			Rec(5, 4, "b4", "b"),
		};
		var rows = await RunBoth(data,
			"events | summarize N = count(), Hi = max(Level), arg_max(Level, Message) by ServiceKey " +
			"| order by ServiceKey asc");

		rows.Should().HaveCount(2);
		rows[0].Should().Equal("a", 3L, 5, 5, "a5");
		rows[1].Should().Equal("b", 2L, 4, 4, "b4");
	}

	// Two-key grouping (the pre-stage partitions by BOTH keys) plus a second arg_max in the same summarize:
	// each call gets its own window ranking.
	[Fact]
	public async Task ArgMax_MultiKey_AndTwoCallsInOneSummarize()
	{
		var data = new[]
		{
			Rec(1, 1, "a1", "a"),
			Rec(2, 3, "a3", "a"),
			Rec(10, 9, "a9", "a"),
			Rec(3, 2, "b2", "b"),
			Rec(4, 8, "b8", "b"),
		};
		// Multi-key: (ServiceKey, Bucket) — group ("a","lo") = ids 1,2 → max Level 3, min 1.
		var multi = await RunBoth(data,
			"events | extend Bucket = iff(Level > 5, 'hi', 'lo') " +
			"| summarize arg_max(Level, Message) by ServiceKey, Bucket | order by ServiceKey asc, Bucket asc");

		multi.Should().HaveCount(4);
		multi[0].Should().Equal("a", "hi", 9, "a9");
		multi[1].Should().Equal("a", "lo", 3, "a3");
		multi[2].Should().Equal("b", "hi", 8, "b8");
		multi[3].Should().Equal("b", "lo", 2, "b2");

		// Two arg picks over ONE pre-stage, each with its own ROW_NUMBER ranking.
		var named = await RunBoth(data,
			"events | summarize arg_max(Level, Message), arg_min(Level, Message) by ServiceKey " +
			"| order by ServiceKey asc");
		named.Should().HaveCount(2);
		named[0].Should().Equal("a", 9, "a9", 1, "a1");
		named[1].Should().Equal("b", 8, "b8", 2, "b2");
	}

	// arg_max alongside percentile: BOTH pre-stage window shapes live in ONE splice (the percentile needs
	// SQLite's ROW_NUMBER/COUNT pair, the arg pick its own ROW_NUMBER). Levels 10,20,30,40: nearest-rank P50
	// → r = ceil(4·50/100) = 2 → 20, and the arg pick is the max row (40).
	[Fact]
	public async Task ArgMax_MixesWithPercentile_InOnePreStage()
	{
		var data = new[]
		{
			Rec(1, 10, "a10"), Rec(2, 20, "a20"), Rec(3, 30, "a30"), Rec(4, 40, "a40"),
		};
		var rows = await RunBoth(data,
			"events | summarize P = percentile(Level, 50), arg_max(Level, Message)");

		rows.Should().HaveCount(1);
		rows[0].Should().Equal(20, 40, "a40");
	}

	// Eager, precise rejections, on BOTH backends: an alias cannot name arg_max's several columns; arity 1
	// has no returned expression; `*` would need the whole-row expansion this engine does not do.
	[Theory]
	[InlineData("events | summarize X = arg_max(Level, Message)", KqlBackend.Sqlite)]
	[InlineData("events | summarize X = arg_max(Level, Message)", KqlBackend.DuckDb)]
	[InlineData("events | summarize arg_max(Level)", KqlBackend.Sqlite)]
	[InlineData("events | summarize arg_max(Level)", KqlBackend.DuckDb)]
	[InlineData("events | summarize arg_max(Level, *)", KqlBackend.Sqlite)]
	[InlineData("events | summarize arg_max(Level, *)", KqlBackend.DuckDb)]
	public async Task ArgMax_UnsupportedForms_ThrowPrecisely(string kql, KqlBackend backend)
	{
		var act = async () => await KqlTestHost.ExecuteAsync([Rec(1, 3, "m")], KustoCode.Parse(kql), backend);
		await act.Should().ThrowAsync<UnsupportedKqlException>();
	}

	// --- the filtered (*if) family ---

	// Every member of the family over ONE dataset: group "a" has matching rows, group "b" has NONE.
	//   aggregate        group "a"                 group "b" (all-false)
	//   avgif            15                        null
	//   minif / maxif    10 / 20                   null
	//   dcountif (Level) 2                         0
	//   count_distinctif 2                         0
	//   countif          2                         0
	[Fact]
	public async Task FilteredFamily_AllFalseGroup_YieldsNullOrZero()
	{
		var data = new[]
		{
			Rec(1, 10, "a1", "a"),
			Rec(2, 20, "a2", "a"),
			Rec(3, 1, "a3", "a"),   // below the predicate
			Rec(4, 4, "b4", "b"),   // group "b": nothing above 5
			Rec(5, 1, "b1", "b"),
		};
		var rows = await RunBoth(data,
			"events | summarize " +
			"  A = avgif(Level, Level > 5), Lo = minif(Level, Level > 5), Hi = maxif(Level, Level > 5), " +
			"  D = dcountif(Level, Level > 5), CD = count_distinctif(Level, Level > 5), " +
			"  N = countif(Level > 5) " +
			"by ServiceKey | order by ServiceKey asc");

		rows.Should().HaveCount(2);
		rows[0].Should().Equal("a", 15.0, 10, 20, 2L, 2L, 2L);
		rows[1].Should().HaveCount(7);
		rows[1][0].Should().Be("b");
		rows[1][1].Should().BeNull();          // avgif over no matching row → null
		rows[1][2].Should().BeNull();          // minif → null
		rows[1][3].Should().BeNull();          // maxif → null
		rows[1][4].Should().Be(0L);            // dcountif → 0
		rows[1][5].Should().Be(0L);            // count_distinctif → 0
		rows[1][6].Should().Be(0L);            // countif → 0
	}

	// Empty input, no-`by`: the *if aggregates hit their empty-input defaults — null for avgif/minif/maxif,
	// 0 for dcountif/count_distinctif, "[]" for make_list_if.
	[Fact]
	public async Task FilteredFamily_EmptyInput_NoBy_YieldsDefaults()
	{
		var data = new[] { Rec(1, 3, "x") };
		var rows = await RunBoth(data,
			"events | where Level > 99 | summarize " +
			"A = avgif(Level, Level > 1), Lo = minif(Level, Level > 1), Hi = maxif(Level, Level > 1), " +
			"D = dcountif(Level, Level > 1), CD = count_distinctif(Level, Level > 1), L = make_list_if(Message, Level > 1)");

		rows.Should().HaveCount(1);
		rows[0].Should().HaveCount(6);
		rows[0][0].Should().BeNull();          // avgif
		rows[0][1].Should().BeNull();          // minif
		rows[0][2].Should().BeNull();          // maxif
		rows[0][3].Should().Be(0L);            // dcountif
		rows[0][4].Should().Be(0L);            // count_distinctif
		rows[0][5].Should().Be("[]");          // make_list_if — the empty array, never null
	}

	// count_distinct is the exact distinct count — the same value as dcount, nulls not counted, and it
	// de-duplicates across the group.
	[Fact]
	public async Task CountDistinct_IsExact_AndIgnoresNulls()
	{
		var data = new[]
		{
			Rec(1, 2, "a", "x", exception: "e1"),
			Rec(2, 2, "b", "x", exception: null),
			Rec(3, 5, "c", "x", exception: "e2"),
			Rec(4, 2, "d", "y"),
		};
		var rows = await RunBoth(data,
			"events | summarize CD = count_distinct(ServiceKey), D = dcount(ServiceKey), " +
			"CDL = count_distinct(Level), CDE = count_distinct(Exception)");

		rows.Should().HaveCount(1);
		// service keys x,y → 2; levels 2,5 → 2; exceptions "e1"/"e2" (the null is never counted) → 2.
		rows[0].Should().Equal(2L, 2L, 2L, 2L);
	}

	// make_list_if/make_set_if keep make_list/make_set's contract (value-ascending, nulls skipped, JSON
	// text) over ONLY the predicate's rows; make_set_if additionally de-duplicates. An all-false group
	// yields "[]" — an empty array, not null.
	[Fact]
	public async Task MakeListIf_MakeSetIf_RestrictToPredicateRows()
	{
		var data = new[]
		{
			Rec(1, 9, "b", "a"),
			Rec(2, 1, "a", "a"),
			Rec(3, 9, "a", "a"),
			Rec(4, 1, "z", "b"),
		};
		var rows = await RunBoth(data,
			"events | summarize L = make_list_if(Message, Level > 5), S = make_set_if(Message, Level > 5) " +
			"by ServiceKey | order by ServiceKey asc");

		rows.Should().HaveCount(2);
		rows[0].Should().Equal("a", "[\"a\",\"b\"]", "[\"a\",\"b\"]");
		rows[1].Should().Equal("b", "[]", "[]");
	}

	// avgif averages ONLY the predicate's rows (the filtered-out row must not drag the average), and a
	// predicate that holds for every row equals the unfiltered aggregate.
	[Fact]
	public async Task AvgIf_AveragesOnlyMatchingRows()
	{
		var data = new[] { Rec(1, 10), Rec(2, 20), Rec(3, 1000) };
		var rows = await RunBoth(data,
			"events | summarize F = avgif(Level, Level < 100), A = avg(Level)");

		rows.Should().HaveCount(1);
		rows[0].Should().Equal(15.0, (10.0 + 20 + 1000) / 3);
	}

	// The predicate must be boolean and the arity exact — a precise rejection rather than a silent 0-row
	// group.
	[Theory]
	[InlineData("events | summarize avgif(Level, Level)", KqlBackend.Sqlite)]
	[InlineData("events | summarize avgif(Level, Level)", KqlBackend.DuckDb)]
	[InlineData("events | summarize minif(Level)", KqlBackend.Sqlite)]
	[InlineData("events | summarize minif(Level)", KqlBackend.DuckDb)]
	public async Task FilteredFamily_UnsupportedForms_ThrowPrecisely(string kql, KqlBackend backend)
	{
		var act = async () => await KqlTestHost.ExecuteAsync([Rec(1, 3, "m")], KustoCode.Parse(kql), backend);
		await act.Should().ThrowAsync<UnsupportedKqlException>();
	}

	// make_list's EMPTY-ARRAY contract holds for a `by` group whose values are all nulls too — "[]", never
	// null (DuckDB's to_json(list(…)) is NULL for an empty list, so the aggregate result is COALESCE'd).
	[Fact]
	public async Task MakeList_AllNullGroup_YieldsEmptyArray()
	{
		var data = new[] { Rec(1, 2, "a", "s"), Rec(2, 2, "a", "s") };
		var rows = await RunBoth(data,
			"events | summarize L = make_list(Exception), S = make_set(Exception) by ServiceKey");

		rows.Should().HaveCount(1);
		rows[0].Should().Equal("s", "[]", "[]");
	}
}
