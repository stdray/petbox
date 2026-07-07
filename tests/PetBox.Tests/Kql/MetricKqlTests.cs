using Kusto.Language;
using PetBox.Log.Core.Metrics;

namespace PetBox.Tests.Kql;

// The `metrics` table root: the SAME KQL subset (the whole kql-coverage operator catalog) over a named
// log's MetricPoints table. These are production-only (the reference executor has no metrics table); they
// pin root routing, metric column addressing (incl. the unified Value, Time/StartTime as datetime, the
// MetricType name form), attribute access, a where/project/summarize/top pipeline, a same-root self-join,
// and structural-error parity with the events/spans roots. SQLite pushdown parity lives in
// MetricSqliteKqlIntegrationTests.
//
// Converted to run production over the SHARED real-SQLite harness (KqlTestHost) instead of the
// EnumerableQuery provider, so the assertions pin the REAL SQL-translated behavior.
public sealed class MetricKqlTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly DateTime Base = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	static long Ns(int startSec) =>
		new DateTimeOffset(Base.AddSeconds(startSec), TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L;

	static MetricPointRecord Metric(
		long id, string name, MetricPointType type, int timeSec,
		double? valueDouble = null, long? valueLong = null,
		long? count = null, double? sum = null, double? min = null, double? max = null,
		int? temporality = null, bool? isMonotonic = null, int? scale = null, long? zeroCount = null,
		string? unit = null, string? description = null, long? startSec = null, int? flags = null,
		string attrs = "{}") => new()
		{
			Id = id,
			MetricName = name,
			MetricType = (int)type,
			Unit = unit,
			Description = description,
			TimeUnixNs = Ns(timeSec),
			StartUnixNs = startSec is { } s ? Ns((int)s) : null,
			Flags = flags,
			ValueDouble = valueDouble,
			ValueLong = valueLong,
			AggregationTemporality = temporality,
			IsMonotonic = isMonotonic,
			Count = count,
			Sum = sum,
			Min = min,
			Max = max,
			Scale = scale,
			ZeroCount = zeroCount,
			AttributesJson = attrs,
		};

	static readonly MetricPointRecord[] Metrics =
	[
		Metric(1, "cpu.usage", MetricPointType.Gauge, 0, valueDouble: 0.4, attrs: """{"host":"eu-1","status_code":200}"""),
		Metric(2, "cpu.usage", MetricPointType.Gauge, 1, valueDouble: 0.9, attrs: """{"host":"eu-2","status_code":500}"""),
		Metric(3, "http.requests", MetricPointType.Sum, 2, valueLong: 120, isMonotonic: true, temporality: 2, unit: "1", attrs: """{"host":"us-1"}"""),
		Metric(4, "http.latency", MetricPointType.Histogram, 3, count: 10, sum: 250.0, min: 5.0, max: 90.0, attrs: """{"host":"us-1"}"""),
	];

	// The one production run seam: seed Metrics into a fresh in-memory LogDb and ExecuteMetrics `ast` over
	// the real linq2db IQueryable, fully materialized. Sqlite is the only Active backend today.
	static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> Run(string kql) =>
		KqlTestHost.ExecuteMetricsAsync(Metrics, Parse(kql), KqlBackend.Sqlite);

	static int Col(IReadOnlyList<KqlColumn> cols, string name)
	{
		for (var i = 0; i < cols.Count; i++)
			if (cols[i].Name == name) return i;
		throw new Xunit.Sdk.XunitException($"column '{name}' not found in [{string.Join(", ", cols.Select(c => c.Name))}]");
	}

	// --- root routing + column shape ---

	[Fact]
	public void MetricPointRecordColumns_HaveExpectedSchema()
	{
		KqlTransformer.MetricPointRecordColumns.Select(c => c.Name).Should().ContainInOrder(
			"MetricName", "MetricType", "TypeName", "Unit", "Description", "Time", "StartTime",
			"Value", "ValueDouble", "ValueLong", "Count", "Sum", "Min", "Max",
			"Temporality", "IsMonotonic", "Scale", "ZeroCount", "Flags", "PropertiesJson");
		// The final bag column MUST be PropertiesJson (shared post-split machinery locates the bag by name).
		KqlTransformer.MetricPointRecordColumns[^1].Name.Should().Be("PropertiesJson");
	}

	[Fact]
	public async Task BareMetrics_YieldsFullMetricColumnShape()
	{
		var (cols, rows) = await Run("metrics");
		cols.Select(c => c.Name).Should().ContainInOrder(
			"MetricName", "MetricType", "TypeName", "Value", "PropertiesJson");
		rows.Should().HaveCount(4);
	}

	[Fact]
	public void GetRootTableName_DistinguishesMetricsRoot()
	{
		KqlTransformer.GetRootTableName(Parse("metrics | where MetricName == 'x'")).Should().Be("metrics");
		KqlTransformer.GetRootTableName(Parse("spans | take 1")).Should().Be("spans");
		KqlTransformer.GetRootTableName(Parse("events | take 1")).Should().Be("events");
	}

	// --- where over first-class metric columns ---

	[Fact]
	public async Task Where_MetricName_Filters()
	{
		var (cols, rows) = await Run("metrics | where MetricName == 'cpu.usage'");
		rows.Select(r => r[Col(cols, "MetricName")]).Should().OnlyContain(n => (string)n! == "cpu.usage");
		rows.Should().HaveCount(2);
	}

	[Fact]
	public async Task Where_MetricType_Filters()
	{
		var (_, rows) = await Run("metrics | where MetricType == 2 | project MetricName");
		rows.Select(r => r[0]).Should().BeEquivalentTo(["http.latency"]);
	}

	[Fact]
	public async Task Where_TypeName_CaseMapping_Filters()
	{
		var (_, rows) = await Run("metrics | where TypeName == 'Histogram' | project MetricName, TypeName");
		rows.Should().ContainSingle();
		rows[0][0].Should().Be("http.latency");
		rows[0][1].Should().Be("Histogram");
	}

	[Fact]
	public async Task Where_Value_NullableNumeric_Filters()
	{
		// Value = COALESCE(ValueDouble, ValueLong); the nullable-column comparison path (not the fast path).
		var (_, rows) = await Run("metrics | where Value > 100 | project MetricName");
		rows.Select(r => r[0]).Should().BeEquivalentTo(["http.requests"]);
	}

	[Fact]
	public async Task Where_TimeAsDatetime_Filters()
	{
		var (_, rows) = await Run("metrics | where Time >= datetime(2026-04-19T10:00:02Z) | project MetricName");
		rows.Select(r => r[0]).Should().BeEquivalentTo(["http.requests", "http.latency"]);
	}

	[Fact]
	public async Task Where_IsMonotonic_NullableBool_Filters()
	{
		var (_, rows) = await Run("metrics | where IsMonotonic == true | project MetricName");
		rows.Select(r => r[0]).Should().BeEquivalentTo(["http.requests"]);
	}

	// --- column types + unified Value ---

	[Fact]
	public async Task Project_ColumnTypes_MatchSchema()
	{
		var (cols, _) = await Run("metrics | project Time, Value, MetricType, TypeName, Count");
		cols[Col(cols, "Time")].ClrType.Should().Be<DateTime>();
		cols[Col(cols, "Value")].ClrType.Should().Be<double?>();
		cols[Col(cols, "MetricType")].ClrType.Should().Be<int>();
		cols[Col(cols, "TypeName")].ClrType.Should().Be<string>();
		cols[Col(cols, "Count")].ClrType.Should().Be<long?>();
	}

	[Fact]
	public async Task Value_UnifiesDoubleAndLongArms()
	{
		var (cols, rows) = await Run(
			"metrics | where MetricName != 'cpu.usage' or Time == datetime(2026-04-19T10:00:00Z) " +
			"| project MetricName, Value, ValueDouble, ValueLong");
		var byName = rows.ToDictionary(r => (string)r[Col(cols, "MetricName")]!, r => r);
		// Gauge point carried ValueDouble.
		((double?)byName["cpu.usage"][Col(cols, "Value")]).Should().Be(0.4);
		// Sum point carried ValueLong → surfaces through the unified Value as a double.
		((double?)byName["http.requests"][Col(cols, "Value")]).Should().Be(120.0);
		((long?)byName["http.requests"][Col(cols, "ValueLong")]).Should().Be(120);
		// Histogram point has neither value arm → Value is null.
		((double?)byName["http.latency"][Col(cols, "Value")]).Should().BeNull();
	}

	// --- attributes: bare-name fallback, Properties.<key>, PropertiesJson bag ---

	[Fact]
	public async Task Attributes_PropertiesPathAndBareFallback()
	{
		(await Run("metrics | where Properties.host == 'us-1' | project MetricName")).Rows.Select(r => r[0])
			.Should().BeEquivalentTo(["http.requests", "http.latency"]);
		(await Run("metrics | where host == 'eu-1' | project MetricName")).Rows.Select(r => r[0])
			.Should().BeEquivalentTo(["cpu.usage"]);
	}

	[Fact]
	public async Task Summarize_ByBareAttributeName_ResolvesFromPropertiesJsonBag()
	{
		// The bag column is named PropertiesJson, so `by host` is addressable by bare name post-split.
		var (cols, rows) = await Run("metrics | summarize C = count() by host");
		cols.Select(c => c.Name).Should().ContainInOrder("host", "C");
		var by = rows.Where(r => r[0] is string).ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		by["eu-1"].Should().Be(1);
		by["us-1"].Should().Be(2);
	}

	[Fact]
	public async Task Attributes_TypedConversion_ComparesNumerically()
	{
		var (_, rows) = await Run("metrics | where toint(Properties.status_code) >= 500 | project MetricName, Value");
		rows.Should().ContainSingle();
		((double?)rows[0][1]).Should().Be(0.9);
	}

	// --- a where/project/summarize pipeline over Value/Count/Sum ---

	[Fact]
	public async Task Summarize_AggregatesOverValueCountSum()
	{
		var (cols, rows) = await Run("metrics | summarize Total = sum(Value), Points = count() by MetricName");
		var by = rows.ToDictionary(r => (string)r[Col(cols, "MetricName")]!, r => r);
		// cpu.usage: 0.4 + 0.9 = 1.3 across two gauge points.
		((double?)by["cpu.usage"][Col(cols, "Total")]).Should().BeApproximately(1.3, 1e-9);
		((long)by["cpu.usage"][Col(cols, "Points")]!).Should().Be(2);
	}

	[Fact]
	public async Task Summarize_SumOverHistogramCountAndSum()
	{
		var (cols, rows) = await Run("metrics | where TypeName == 'Histogram' | summarize C = sum(Count), S = sum(Sum)");
		rows.Should().ContainSingle();
		((long?)rows[0][Col(cols, "C")]).Should().Be(10);
		((double?)rows[0][Col(cols, "S")]).Should().Be(250.0);
	}

	[Fact]
	public async Task Top_ByValue_SortsDescending()
	{
		var (cols, rows) = await Run("metrics | where MetricName == 'cpu.usage' | project MetricName, Value | top 2 by Value desc");
		rows.Select(r => (double?)r[Col(cols, "Value")]).Should().ContainInOrder(0.9, 0.4);
	}

	[Fact]
	public async Task Extend_ComputedColumn_OverMetrics()
	{
		var (_, rows) = await Run("metrics | where MetricName == 'http.requests' | extend Hot = Value > 100 | project Hot");
		rows[0][0].Should().Be(true);
	}

	// --- correlation op from the catalog: self-join over the SAME metrics root (cross-signal / cross-root
	// joins are out of scope by design — the single SQLite context enables same-root joins here) ---

	[Fact]
	public async Task Join_SameMetricsRoot_OnMetricName()
	{
		// pair each cpu.usage point with cpu.usage points sharing the metric name.
		var (cols, rows) = await Run(
			"metrics | where MetricType == 0 | join kind=inner (metrics | where MetricType == 0) on MetricName | project MetricName, MetricName1");
		// two gauge points self-join on MetricName → 2x2 = 4 pairs.
		rows.Should().HaveCount(4);
		rows.Should().OnlyContain(r => (string)r[Col(cols, "MetricName")]! == "cpu.usage");
	}

	// --- structural-error parity: an unsupported construct over metrics errors the same way as events ---

	[Fact]
	public async Task UnsupportedOperator_OverMetrics_ThrowsSameAsEvents()
	{
		var metricAct = async () => await Run("metrics | sample 3");
		var eventAct = async () => await KqlTestHost.ExecuteAsync(
			Array.Empty<LogEntryRecord>(), Parse("events | sample 3"), KqlBackend.Sqlite);

		var metricEx = (await metricAct.Should().ThrowAsync<UnsupportedKqlException>()).Which;
		var eventEx = (await eventAct.Should().ThrowAsync<UnsupportedKqlException>()).Which;
		metricEx.Message.Should().Be(eventEx.Message); // same structural error text
	}

	[Fact]
	public async Task UnknownTable_ListsAllRoots()
	{
		var act = async () => await KqlTestHost.ExecuteAsync(
			Array.Empty<LogEntryRecord>(), Parse("bogus | take 1"), KqlBackend.Sqlite);
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*events*spans*metrics*");
	}
}
