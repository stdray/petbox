using Kusto.Language;

namespace PetBox.Tests.Kql;

// Production-side pins for `summarize percentile(expr, P)` — EXACT NEAREST-RANK (spec kql-percentile,
// owner decision): for a group's non-null values sorted ascending v[1..n], rank r = MAX(1, ceil(n·P/100)),
// result = v[r] (an ACTUAL value from the set — never interpolated). percentile is deliberately EXCLUDED
// from the KustoLoco differential (KustoLoco computes a type-5 INTERPOLATED percentile); correctness is
// pinned here instead by (a) SQLite == DuckDB value-exact cross-backend equality on EVERY query and
// (b) hand-computed nearest-rank expectations from the MAX(1, ceil(n·P/100)) formula.
public sealed class KqlPercentileTests
{
	static LogEntryRecord Rec(long id, int level = 2, string serviceKey = "s", string props = "{}") => new()
	{
		Id = id,
		Level = level,
		Message = "m",
		MessageTemplate = "m",
		ServiceKey = serviceKey,
		PropertiesJson = props,
		TimestampMs = id,
	};

	// Runs the query on BOTH backends, asserts the results are value-exact equal (the cross-backend pin
	// that replaces the KustoLoco differential), and returns the rows for the hand-computed assertions.
	static async Task<List<object?[]>> RunBoth(IReadOnlyList<LogEntryRecord> data, string kql)
	{
		var code = KustoCode.Parse(kql);
		var (_, sqlite) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.Sqlite);
		var (_, duck) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.DuckDb);
		duck.Should().BeEquivalentTo(sqlite, o => o.WithStrictOrdering(),
			"percentile must be value-exact across SQLite and DuckDB (the cross-backend correctness pin)");
		return sqlite;
	}

	// Odd n: [1,3,5,7,9], P=50 → r = ceil(5·50/100) = 3 → 5 (the true median element).
	[Fact]
	public async Task OddN_P50_PicksMiddleValue()
	{
		var data = new[] { Rec(1, 9), Rec(2, 1), Rec(3, 5), Rec(4, 3), Rec(5, 7) };
		var rows = await RunBoth(data, "events | summarize P = percentile(Level, 50)");

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(5);
	}

	// Even n: [1,3,5,7], P=50 → r = ceil(4·50/100) = 2 → 3. Nearest-rank picks a REAL element — NOT the
	// interpolated 4 KustoLoco/type-5 would produce (which is exactly why percentile is out of the differential).
	[Fact]
	public async Task EvenN_P50_NearestRank_NoInterpolation()
	{
		var data = new[] { Rec(1, 7), Rec(2, 1), Rec(3, 5), Rec(4, 3) };
		var rows = await RunBoth(data, "events | summarize P = percentile(Level, 50)");

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(3);
	}

	// Duplicates: [1,2,2,2,9], P=50 → r = 3 → 2 (rank counts duplicate values as distinct positions).
	[Fact]
	public async Task Duplicates_CountTowardRank()
	{
		var data = new[] { Rec(1, 2), Rec(2, 9), Rec(3, 2), Rec(4, 1), Rec(5, 2) };
		var rows = await RunBoth(data, "events | summarize P = percentile(Level, 50)");

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(2);
	}

	// The rank formula's boundaries over v = [1..10] (n=10): P=0 clamps to r=1 (min); integer-division
	// ceiling steps exactly at multiples of 10 (P=10 → r=1, P=11 → r=2); P=100 → r=10 (max).
	[Theory]
	[InlineData(0, 1)]     // clamp: MAX(1, 0) → min
	[InlineData(10, 1)]    // ceil(1.0)  = 1
	[InlineData(11, 2)]    // ceil(1.1)  = 2
	[InlineData(50, 5)]    // ceil(5.0)  = 5
	[InlineData(90, 9)]    // ceil(9.0)  = 9
	[InlineData(95, 10)]   // ceil(9.5)  = 10
	[InlineData(100, 10)]  // ceil(10.0) = 10 → max
	public async Task RankFormula_Boundaries(int p, int expected)
	{
		var data = Enumerable.Range(1, 10).Select(i => Rec(i, i)).ToArray();
		var rows = await RunBoth(data, $"events | summarize P = percentile(Level, {p})");

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(expected);
	}

	// A fractional P: [1,2,3], P=99.9 → r = ceil(3·0.999) = ceil(2.997) = 3.
	[Fact]
	public async Task FractionalP_Works()
	{
		var data = new[] { Rec(1, 1), Rec(2, 2), Rec(3, 3) };
		var rows = await RunBoth(data, "events | summarize P = percentile(Level, 99.9)");

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(3);
	}

	// Multiple percentiles in ONE summarize (sharing one window per distinct arg on SQLite), P=0 → min and
	// P=100 → max: v = [4,8,15,16,23], P50 → r=3 → 15.
	[Fact]
	public async Task MultiplePercentiles_P0Min_P100Max()
	{
		var data = new[] { Rec(1, 16), Rec(2, 4), Rec(3, 23), Rec(4, 8), Rec(5, 15) };
		var rows = await RunBoth(data,
			"events | summarize Lo = percentile(Level, 0), Med = percentile(Level, 50), Hi = percentile(Level, 100)");

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(4);   // P=0 → min
		rows[0][1].Should().Be(15);  // r = ceil(2.5) = 3
		rows[0][2].Should().Be(23);  // P=100 → max
	}

	// Nulls are IGNORED (group "a": v=[10,20,30] + one null row → n=3, r=2 → 20) and an all-null group
	// yields NULL (group "b"). Also exercises the SQLite window pre-stage over an EMITTED upstream (extend).
	[Fact]
	public async Task NullsIgnored_AllNullGroupYieldsNull()
	{
		var data = new[]
		{
			Rec(1, serviceKey: "a", props: """{"v":"30"}"""),
			Rec(2, serviceKey: "a", props: """{"v":"10"}"""),
			Rec(3, serviceKey: "a"),                          // v missing → null, ignored
			Rec(4, serviceKey: "a", props: """{"v":"20"}"""),
			Rec(5, serviceKey: "b"),                          // all-null group
			Rec(6, serviceKey: "b"),
		};
		var rows = await RunBoth(data,
			"events | extend V = tolong(Properties.v) | summarize P = percentile(V, 50) by ServiceKey | order by ServiceKey asc");

		rows.Should().HaveCount(2);
		rows[0][0].Should().Be("a");
		rows[0][1].Should().Be(20L);
		rows[1][0].Should().Be("b");
		rows[1][1].Should().BeNull();
	}

	// Empty input, no-`by`: Kusto's one-default-row rule — a single row whose percentile is null.
	[Fact]
	public async Task EmptyInput_NoBy_YieldsNull()
	{
		var data = new[] { Rec(1) };
		var rows = await RunBoth(data, "events | where Level == 999 | summarize P = percentile(Level, 50)");

		rows.Should().HaveCount(1);
		rows[0][0].Should().BeNull();
	}

	// Multi-key grouping: the window partitions by BOTH keys, exactly like the GROUP BY.
	//   ("a",1): ids [1,2,3] → r=2 → 2;  ("a",2): ids [10,20] → r=1 → 10;  ("b",1): [5] → 5.
	[Fact]
	public async Task MultiKey_GroupsIndependently()
	{
		var data = new[]
		{
			Rec(3, 1, "a"), Rec(1, 1, "a"), Rec(2, 1, "a"),
			Rec(20, 2, "a"), Rec(10, 2, "a"),
			Rec(5, 1, "b"),
		};
		var rows = await RunBoth(data,
			"events | summarize P = percentile(Id, 50) by ServiceKey, Level | order by ServiceKey asc, Level asc");

		rows.Should().HaveCount(3);
		rows[0].Should().Equal("a", 1, 2L);
		rows[1].Should().Equal("a", 2, 10L);
		rows[2].Should().Equal("b", 1, 5L);
	}

	// datetime percentile: nearest-rank picks a REAL stored timestamp (epoch-ms round-trips through
	// StorageToLogical) — timestamps [1000,2000,5000], P=50 → r=2 → 2000ms.
	[Fact]
	public async Task Datetime_PicksRealTimestamp()
	{
		var data = new[] { Rec(5000), Rec(1000), Rec(2000) };
		var rows = await RunBoth(data, "events | summarize P = percentile(Timestamp, 50)");

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(2000).UtcDateTime);
	}

	// percentile composes with other aggregates in the same summarize (the pre-stage passes every upstream
	// column through, so count/max recompile unchanged).
	[Fact]
	public async Task MixesWithOtherAggregates()
	{
		var data = new[] { Rec(1, 1, "a"), Rec(2, 3, "a"), Rec(3, 5, "a"), Rec(4, 9, "b") };
		var rows = await RunBoth(data,
			"events | summarize N = count(), P = percentile(Level, 50), M = max(Level) by ServiceKey | order by ServiceKey asc");

		rows.Should().HaveCount(2);
		rows[0].Should().Equal("a", 3L, 3, 5);  // n=3, r=2 → 3
		rows[1].Should().Equal("b", 1L, 9, 9);
	}

	// Unsupported forms are eager, precise errors on BOTH backends: bad arg type (string), wrong arity,
	// non-literal / out-of-range P, and the multi-value percentiles() form.
	[Theory]
	[InlineData("events | summarize P = percentile(Message, 50)", KqlBackend.Sqlite)]
	[InlineData("events | summarize P = percentile(Message, 50)", KqlBackend.DuckDb)]
	[InlineData("events | summarize P = percentile(Level)", KqlBackend.Sqlite)]
	[InlineData("events | summarize P = percentile(Level)", KqlBackend.DuckDb)]
	[InlineData("events | summarize P = percentile(Level, 50, 95)", KqlBackend.Sqlite)]
	[InlineData("events | summarize P = percentile(Level, 50, 95)", KqlBackend.DuckDb)]
	[InlineData("events | summarize P = percentile(Level, Id)", KqlBackend.Sqlite)]
	[InlineData("events | summarize P = percentile(Level, Id)", KqlBackend.DuckDb)]
	[InlineData("events | summarize P = percentile(Level, 101)", KqlBackend.Sqlite)]
	[InlineData("events | summarize P = percentile(Level, 101)", KqlBackend.DuckDb)]
	[InlineData("events | summarize P = percentiles(Level, 50, 95)", KqlBackend.Sqlite)]
	[InlineData("events | summarize P = percentiles(Level, 50, 95)", KqlBackend.DuckDb)]
	public async Task UnsupportedForms_Throw(string kql, KqlBackend backend)
	{
		var data = new[] { Rec(1) };
		var code = KustoCode.Parse(kql);

		var act = async () => await KqlTestHost.ExecuteAsync(data, code, backend);

		await act.Should().ThrowAsync<UnsupportedKqlException>();
	}
}
