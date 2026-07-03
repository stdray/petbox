using Kusto.Language;

namespace PetBox.Tests.Kql;

// Production-only coverage for the KQL datetime functions.
//   * now()/ago() need a pinned clock (KustoLoco owns its own now()), so they run against the
//     production engine with an injected TimeProvider.
//   * datetime_diff() uses period-boundary semantics (matching real Kusto — see the canonical
//     doc examples below). KustoLoco is not a usable oracle here: it tz-shifts datetime() literals
//     by the host offset and truncates raw differences, so it disagrees with real Kusto off-UTC.
public sealed class KqlDateTimeTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	// A fixed instant so now()/ago() are deterministic.
	static readonly DateTime PinnedNow = new(2026, 4, 19, 12, 0, 0, DateTimeKind.Utc);
	static readonly TimeProvider Clock = new FixedClock(PinnedNow);

	sealed class FixedClock(DateTime utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
	}

	static LogEntryRecord At(long id, DateTime ts) => new()
	{
		Id = id,
		TimestampMs = new DateTimeOffset(ts, TimeSpan.Zero).ToUnixTimeMilliseconds(),
		Level = (int)LogLevel.Information,
		Message = "m",
		ServiceKey = "svc",
	};

	static readonly LogEntryRecord[] Rows =
	[
		At(1, new DateTime(2026, 4, 19, 11, 0, 0, DateTimeKind.Utc)),  // 1h before now
		At(2, new DateTime(2026, 4, 19, 11, 45, 0, DateTimeKind.Utc)), // 15m before now
		At(3, new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc)),  // 1d before now
		At(4, new DateTime(2026, 4, 19, 11, 59, 30, DateTimeKind.Utc)),// 30s before now
	];

	static IReadOnlyList<long> WhereIds(string kql) =>
		KqlTransformer.Apply(Rows.AsQueryable(), Parse(kql), Clock).ToList().Select(r => r.Id).ToList();

	static async Task<List<object?[]>> Table(string kql, IReadOnlyList<LogEntryRecord>? data = null)
	{
		var result = KqlTransformer.Execute((data ?? Rows).AsQueryable(), Parse(kql), Clock);
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		return rows;
	}

	[Fact]
	public async Task Now_ResolvesToPinnedClock()
	{
		var rows = await Table("events | where Id == 1 | project N = now()");
		rows[0][0].Should().Be(PinnedNow);
	}

	[Fact]
	public void Ago_InWhere_FiltersRelativeToNow()
	{
		// within the last 20 minutes → ids 2 (15m) and 4 (30s)
		WhereIds("events | where Timestamp > ago(20m)").Should().BeEquivalentTo([2L, 4L]);
		// within the last 2 hours → all but the 1-day-old row
		WhereIds("events | where Timestamp > ago(2h)").Should().BeEquivalentTo([1L, 2L, 4L]);
		// within the last 2 days → everything
		WhereIds("events | where Timestamp >= ago(2d)").Should().BeEquivalentTo([1L, 2L, 3L, 4L]);
	}

	[Fact]
	public async Task NowMinusColumn_DateTimeDiff_MeasuresAge()
	{
		// minute boundaries crossed between each row and now() (12:00). Boundary semantics count the
		// minute-index difference, so 11:59:30 → 12:00:00 is one boundary, not zero.
		var rows = await Table("events | project Id, Age = datetime_diff('minute', now(), Timestamp)");
		var byId = rows.ToDictionary(r => (long)r[0]!, r => (long)r[1]!);
		byId[1].Should().Be(60);        // 11:00
		byId[2].Should().Be(15);        // 11:45
		byId[3].Should().Be(24 * 60);   // 12:00 previous day
		byId[4].Should().Be(1);         // 11:59:30 → crosses the 12:00 minute boundary
	}

	[Fact]
	public async Task StartOf_ProducesPeriodBoundaries()
	{
		// 2026-04-19 is a Sunday, so startofweek == startofday for this date.
		var data = new[] { At(1, new DateTime(2026, 4, 19, 13, 37, 42, DateTimeKind.Utc)) };
		var rows = await Table(
			"events | project D = startofday(Timestamp), W = startofweek(Timestamp), M = startofmonth(Timestamp), Y = startofyear(Timestamp)",
			data);
		rows[0][0].Should().Be(new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc));
		rows[0][1].Should().Be(new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc));
		rows[0][2].Should().Be(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
		rows[0][3].Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	[Fact]
	public async Task StartOfWeek_AnchorsOnSunday()
	{
		// 2026-04-22 is a Wednesday → startofweek is the preceding Sunday, 2026-04-19.
		var data = new[] { At(1, new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc)) };
		var rows = await Table("events | project W = startofweek(Timestamp)", data);
		rows[0][0].Should().Be(new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc));
	}

	// The canonical Kusto documentation examples — these define real Kusto's period-boundary
	// semantics and are exactly what our implementation reproduces.
	[Theory]
	[InlineData("datetime_diff('year', datetime(2017-01-01), datetime(2000-01-01))", 17L)]
	[InlineData("datetime_diff('quarter', datetime(2017-07-01), datetime(2017-03-30))", 2L)]
	[InlineData("datetime_diff('month', datetime(2017-01-01), datetime(2015-12-31))", 13L)]
	[InlineData("datetime_diff('week', datetime(2017-10-29 00:00), datetime(2017-09-30 23:59))", 5L)]
	[InlineData("datetime_diff('day', datetime(2017-10-29 00:00), datetime(2017-09-30 23:59))", 29L)]
	[InlineData("datetime_diff('hour', datetime(2017-10-31 01:00), datetime(2017-10-30 23:59))", 2L)]
	[InlineData("datetime_diff('minute', datetime(2017-10-30 23:05:01), datetime(2017-10-30 23:00:59))", 5L)]
	[InlineData("datetime_diff('second', datetime(2017-01-01 00:00:10), datetime(2017-01-01 00:00:00.1))", 10L)]
	public async Task DateTimeDiff_MatchesKustoDocExamples(string expr, long expected)
	{
		var rows = await Table($"events | where Id == 1 | project N = {expr}");
		rows[0][0].Should().Be(expected);
	}

	[Fact]
	public async Task DateTimeDiff_IsSignedAndComposesWithColumns()
	{
		// negative when datetime1 < datetime2
		var neg = await Table("events | where Id == 1 | project N = datetime_diff('day', datetime(2017-01-01), datetime(2017-01-05))");
		neg[0][0].Should().Be(-4L);

		// minutes into the day, computed from the column and its own startofday
		var age = await Table("events | project Id, N = datetime_diff('minute', Timestamp, startofday(Timestamp))");
		var byId = age.ToDictionary(r => (long)r[0]!, r => (long)r[1]!);
		byId[1].Should().Be(11 * 60);       // 11:00
		byId[3].Should().Be(12 * 60);       // 12:00 previous day
	}

	[Theory]
	[InlineData("events | project N = now(1)", "*now()*no arguments*")]
	[InlineData("events | project N = ago()", "*ago()*1 argument*")]
	[InlineData("events | project N = ago('x')", "*ago()*timespan*")]
	[InlineData("events | project N = startofday()", "*startofday()*1 argument*")]
	[InlineData("events | project N = startofday(Message)", "*datetime*")]
	[InlineData("events | project N = datetime_diff('day', Timestamp)", "*datetime_diff()*3 arguments*")]
	[InlineData("events | project N = datetime_diff('fortnight', now(), Timestamp)", "*fortnight*not supported*")]
	public void InvalidDateTimeCalls_ThrowPrecise(string kql, string message)
	{
		var act = () =>
		{
			var result = KqlTransformer.Execute(Rows.AsQueryable(), Parse(kql), Clock);
			_ = result.Columns;
		};
		act.Should().Throw<UnsupportedKqlException>().WithMessage(message);
	}
}
