using Kusto.Language;
using PetBox.Log.Core.Metrics;

namespace PetBox.Tests.Kql;

// The `metrics` table root: the SAME KQL subset (the whole kql-coverage operator catalog) over a named
// log's MetricPoints table. These are production-only (the reference executor has no metrics table); they
// pin root routing, metric column addressing (incl. the unified Value, Time/StartTime as datetime, the
// MetricType name form), attribute access, a where/project/summarize/top pipeline, a same-root self-join,
// and structural-error parity with the events/spans roots. SQLite pushdown parity lives in
// MetricSqliteKqlIntegrationTests.
public sealed class MetricKqlTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly DateTime Base = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	static long Ns(int startSec) =>
		new DateTimeOffset(Base.AddSeconds(startSec), TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L;

	static MetricPointRecord Metric(
		string name, MetricPointType type, int timeSec,
		double? valueDouble = null, long? valueLong = null,
		long? count = null, double? sum = null, double? min = null, double? max = null,
		int? temporality = null, bool? isMonotonic = null, int? scale = null, long? zeroCount = null,
		string? unit = null, string? description = null, long? startSec = null, int? flags = null,
		string attrs = "{}") => new()
		{
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
		Metric("cpu.usage", MetricPointType.Gauge, 0, valueDouble: 0.4, attrs: """{"host":"eu-1","status_code":200}"""),
		Metric("cpu.usage", MetricPointType.Gauge, 1, valueDouble: 0.9, attrs: """{"host":"eu-2","status_code":500}"""),
		Metric("http.requests", MetricPointType.Sum, 2, valueLong: 120, isMonotonic: true, temporality: 2, unit: "1", attrs: """{"host":"us-1"}"""),
		Metric("http.latency", MetricPointType.Histogram, 3, count: 10, sum: 250.0, min: 5.0, max: 90.0, attrs: """{"host":"us-1"}"""),
	];

	static IQueryable<MetricPointRecord> Src() => Metrics.AsQueryable();

	static KqlResult Exec(string kql) => KqlTransformer.ExecuteMetrics(Src(), Parse(kql));

	static async Task<List<object?[]>> Materialize(KqlResult result)
	{
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		return rows;
	}

	static int Col(KqlResult r, string name)
	{
		for (var i = 0; i < r.Columns.Count; i++)
			if (r.Columns[i].Name == name) return i;
		throw new Xunit.Sdk.XunitException($"column '{name}' not found in [{string.Join(", ", r.Columns.Select(c => c.Name))}]");
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
		var result = Exec("metrics");
		result.Columns.Select(c => c.Name).Should().ContainInOrder(
			"MetricName", "MetricType", "TypeName", "Value", "PropertiesJson");
		var rows = await Materialize(result);
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
		var rows = await Materialize(Exec("metrics | where MetricName == 'cpu.usage'"));
		rows.Select(r => r[Col(Exec("metrics"), "MetricName")]).Should().OnlyContain(n => (string)n! == "cpu.usage");
		rows.Should().HaveCount(2);
	}

	[Fact]
	public async Task Where_MetricType_Filters()
	{
		var result = Exec("metrics | where MetricType == 2 | project MetricName");
		var rows = await Materialize(result);
		rows.Select(r => r[0]).Should().BeEquivalentTo(["http.latency"]);
	}

	[Fact]
	public async Task Where_TypeName_CaseMapping_Filters()
	{
		var result = Exec("metrics | where TypeName == 'Histogram' | project MetricName, TypeName");
		var rows = await Materialize(result);
		rows.Should().ContainSingle();
		rows[0][0].Should().Be("http.latency");
		rows[0][1].Should().Be("Histogram");
	}

	[Fact]
	public async Task Where_Value_NullableNumeric_Filters()
	{
		// Value = COALESCE(ValueDouble, ValueLong); the nullable-column comparison path (not the fast path).
		var result = Exec("metrics | where Value > 100 | project MetricName");
		var rows = await Materialize(result);
		rows.Select(r => r[0]).Should().BeEquivalentTo(["http.requests"]);
	}

	[Fact]
	public async Task Where_TimeAsDatetime_Filters()
	{
		var result = Exec("metrics | where Time >= datetime(2026-04-19T10:00:02Z) | project MetricName");
		var rows = await Materialize(result);
		rows.Select(r => r[0]).Should().BeEquivalentTo(["http.requests", "http.latency"]);
	}

	[Fact]
	public async Task Where_IsMonotonic_NullableBool_Filters()
	{
		var result = Exec("metrics | where IsMonotonic == true | project MetricName");
		var rows = await Materialize(result);
		rows.Select(r => r[0]).Should().BeEquivalentTo(["http.requests"]);
	}

	// --- column types + unified Value ---

	[Fact]
	public void Project_ColumnTypes_MatchSchema()
	{
		var result = Exec("metrics | project Time, Value, MetricType, TypeName, Count");
		result.Columns[Col(result, "Time")].ClrType.Should().Be<DateTime>();
		result.Columns[Col(result, "Value")].ClrType.Should().Be<double?>();
		result.Columns[Col(result, "MetricType")].ClrType.Should().Be<int>();
		result.Columns[Col(result, "TypeName")].ClrType.Should().Be<string>();
		result.Columns[Col(result, "Count")].ClrType.Should().Be<long?>();
	}

	[Fact]
	public async Task Value_UnifiesDoubleAndLongArms()
	{
		var result = Exec(
			"metrics | where MetricName != 'cpu.usage' or Time == datetime(2026-04-19T10:00:00Z) " +
			"| project MetricName, Value, ValueDouble, ValueLong");
		var rows = await Materialize(result);
		var byName = rows.ToDictionary(r => (string)r[Col(result, "MetricName")]!, r => r);
		// Gauge point carried ValueDouble.
		((double?)byName["cpu.usage"][Col(result, "Value")]).Should().Be(0.4);
		// Sum point carried ValueLong → surfaces through the unified Value as a double.
		((double?)byName["http.requests"][Col(result, "Value")]).Should().Be(120.0);
		((long?)byName["http.requests"][Col(result, "ValueLong")]).Should().Be(120);
		// Histogram point has neither value arm → Value is null.
		((double?)byName["http.latency"][Col(result, "Value")]).Should().BeNull();
	}

	// --- attributes: bare-name fallback, Properties.<key>, PropertiesJson bag ---

	[Fact]
	public async Task Attributes_PropertiesPathAndBareFallback()
	{
		(await Materialize(Exec("metrics | where Properties.host == 'us-1' | project MetricName"))).Select(r => r[0])
			.Should().BeEquivalentTo(["http.requests", "http.latency"]);
		(await Materialize(Exec("metrics | where host == 'eu-1' | project MetricName"))).Select(r => r[0])
			.Should().BeEquivalentTo(["cpu.usage"]);
	}

	[Fact]
	public async Task Summarize_ByBareAttributeName_ResolvesFromPropertiesJsonBag()
	{
		// The bag column is named PropertiesJson, so `by host` is addressable by bare name post-split.
		var result = Exec("metrics | summarize C = count() by host");
		result.Columns.Select(c => c.Name).Should().ContainInOrder("host", "C");
		var rows = await Materialize(result);
		var by = rows.Where(r => r[0] is string).ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		by["eu-1"].Should().Be(1);
		by["us-1"].Should().Be(2);
	}

	[Fact]
	public async Task Attributes_TypedConversion_ComparesNumerically()
	{
		var result = Exec("metrics | where toint(Properties.status_code) >= 500 | project MetricName, Value");
		var rows = await Materialize(result);
		rows.Should().ContainSingle();
		((double?)rows[0][1]).Should().Be(0.9);
	}

	// --- a where/project/summarize pipeline over Value/Count/Sum ---

	[Fact]
	public async Task Summarize_AggregatesOverValueCountSum()
	{
		var result = Exec("metrics | summarize Total = sum(Value), Points = count() by MetricName");
		var rows = await Materialize(result);
		var by = rows.ToDictionary(r => (string)r[Col(result, "MetricName")]!, r => r);
		// cpu.usage: 0.4 + 0.9 = 1.3 across two gauge points.
		((double?)by["cpu.usage"][Col(result, "Total")]).Should().BeApproximately(1.3, 1e-9);
		((long)by["cpu.usage"][Col(result, "Points")]!).Should().Be(2);
	}

	[Fact]
	public async Task Summarize_SumOverHistogramCountAndSum()
	{
		var result = Exec("metrics | where TypeName == 'Histogram' | summarize C = sum(Count), S = sum(Sum)");
		var rows = await Materialize(result);
		rows.Should().ContainSingle();
		((long?)rows[0][Col(result, "C")]).Should().Be(10);
		((double?)rows[0][Col(result, "S")]).Should().Be(250.0);
	}

	[Fact]
	public async Task Top_ByValue_SortsDescending()
	{
		var result = Exec("metrics | where MetricName == 'cpu.usage' | project MetricName, Value | top 2 by Value desc");
		var rows = await Materialize(result);
		rows.Select(r => (double?)r[Col(result, "Value")]).Should().ContainInOrder(0.9, 0.4);
	}

	[Fact]
	public async Task Extend_ComputedColumn_OverMetrics()
	{
		var result = Exec("metrics | where MetricName == 'http.requests' | extend Hot = Value > 100 | project Hot");
		var rows = await Materialize(result);
		rows[0][0].Should().Be(true);
	}

	// --- correlation op from the catalog: self-join over the SAME metrics root (cross-signal / cross-root
	// joins are out of scope by design — the single SQLite context enables same-root joins here) ---

	[Fact]
	public async Task Join_SameMetricsRoot_OnMetricName()
	{
		// pair each cpu.usage point with cpu.usage points sharing the metric name.
		var result = Exec(
			"metrics | where MetricType == 0 | join kind=inner (metrics | where MetricType == 0) on MetricName | project MetricName, MetricName1");
		var rows = await Materialize(result);
		// two gauge points self-join on MetricName → 2x2 = 4 pairs.
		rows.Should().HaveCount(4);
		rows.Should().OnlyContain(r => (string)r[Col(result, "MetricName")]! == "cpu.usage");
	}

	// --- structural-error parity: an unsupported construct over metrics errors the same way as events ---

	[Fact]
	public void UnsupportedOperator_OverMetrics_ThrowsSameAsEvents()
	{
		var metricAct = () => KqlTransformer.ExecuteMetrics(Src(), Parse("metrics | sample 3"));
		var eventAct = () => KqlTransformer.Execute(Array.Empty<LogEntryRecord>().AsQueryable(), Parse("events | sample 3"));

		var metricEx = metricAct.Should().Throw<UnsupportedKqlException>().Which;
		var eventEx = eventAct.Should().Throw<UnsupportedKqlException>().Which;
		metricEx.Message.Should().Be(eventEx.Message); // same structural error text
	}

	[Fact]
	public void UnknownTable_ListsAllRoots()
	{
		var act = () => KqlTransformer.Execute(Array.Empty<LogEntryRecord>().AsQueryable(), Parse("bogus | take 1"));
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*events*spans*metrics*");
	}
}
