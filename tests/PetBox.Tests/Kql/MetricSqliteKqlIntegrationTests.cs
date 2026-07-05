using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using PetBox.Log.Core.Metrics;

namespace PetBox.Tests.Kql;

// SQLite pushdown parity for the `metrics` root: the pre-split where/order/top/summarize runs as real
// SQLite SQL against a real MetricPoints table (mirroring SpanSqliteKqlIntegrationTests for spans).
// Metric-specific mappings exercised here: MetricType int + the computed TypeName CASE column, the
// unified Value = COALESCE(ValueDouble, ValueLong) (nullable-column comparison on the SQL path), Time as
// an epoch-ms-derived datetime column, and attributes via json_extract over AttributesJson.
public sealed class MetricSqliteKqlIntegrationTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly string _dbPath;
	LogDb _logDb = null!;

	public MetricSqliteKqlIntegrationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-kql-metric-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "test.db");
	}

	public async Task InitializeAsync()
	{
		var connStr = $"Data Source={_dbPath};Cache=Shared";
		LogSchema.Ensure(connStr); // creates LogEntries + Spans + MetricPoints (idempotent)
		_logDb = new LogDb(LogDb.CreateOptions(connStr));
		await _logDb.MetricPoints.BulkCopyAsync(Seed);
	}

	public Task DisposeAsync()
	{
		_logDb?.Dispose();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	static readonly DateTime Base = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	static long Ns(int startSec) =>
		new DateTimeOffset(Base.AddSeconds(startSec), TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L;

	static MetricPointRecord Metric(
		string name, MetricPointType type, int timeSec,
		double? valueDouble = null, long? valueLong = null,
		long? count = null, double? sum = null, bool? isMonotonic = null,
		string attrs = "{}") => new()
		{
			MetricName = name,
			MetricType = (int)type,
			TimeUnixNs = Ns(timeSec),
			ValueDouble = valueDouble,
			ValueLong = valueLong,
			Count = count,
			Sum = sum,
			IsMonotonic = isMonotonic,
			AttributesJson = attrs,
		};

	static readonly MetricPointRecord[] Seed =
	[
		Metric("cpu.usage", MetricPointType.Gauge, 0, valueDouble: 0.4, attrs: """{"host":"eu-1","status_code":200}"""),
		Metric("cpu.usage", MetricPointType.Gauge, 1, valueDouble: 0.9, attrs: """{"host":"eu-2","status_code":500}"""),
		Metric("http.requests", MetricPointType.Sum, 2, valueLong: 120, isMonotonic: true, attrs: """{"host":"us-1"}"""),
		Metric("http.latency", MetricPointType.Histogram, 3, count: 10, sum: 250.0, attrs: """{"host":"us-1"}"""),
	];

	// Metric points have no natural string key column; project MetricName + Time and read back the names.
	async Task<IReadOnlyList<string>> NamesAsync(string kql)
	{
		var code = KustoCode.Parse(kql + " | project MetricName, Time");
		var result = KqlTransformer.ExecuteMetrics(_logDb.MetricPoints, code);
		var names = new List<string>();
		await foreach (var row in result.Rows)
			names.Add((string)row[0]!);
		return names;
	}

	[Fact]
	public async Task WhereMetricName_TranslatesToSql()
	{
		(await NamesAsync("metrics | where MetricName == 'cpu.usage'"))
			.Should().BeEquivalentTo(["cpu.usage", "cpu.usage"]);
	}

	[Fact]
	public async Task WhereMetricType_TranslatesToSql()
	{
		(await NamesAsync("metrics | where MetricType == 1")).Should().BeEquivalentTo(["http.requests"]);
	}

	[Fact]
	public async Task WhereTypeName_CaseColumn_TranslatesToSql()
	{
		(await NamesAsync("metrics | where TypeName == 'Histogram'")).Should().BeEquivalentTo(["http.latency"]);
		(await NamesAsync("metrics | where TypeName == 'Gauge'")).Should().BeEquivalentTo(["cpu.usage", "cpu.usage"]);
	}

	[Fact]
	public async Task WhereValue_NullableCoalesce_TranslatesToSql()
	{
		// Value = COALESCE(ValueDouble, ValueLong); the long arm (120) and the double arm (0.4/0.9) are
		// both filtered by the same numeric column, entirely in SQL.
		(await NamesAsync("metrics | where Value > 100")).Should().BeEquivalentTo(["http.requests"]);
		(await NamesAsync("metrics | where Value < 1")).Should().BeEquivalentTo(["cpu.usage", "cpu.usage"]);
	}

	[Fact]
	public async Task WhereTimeDatetime_TranslatesToSql()
	{
		(await NamesAsync("metrics | where Time >= datetime(2026-04-19T10:00:02Z)"))
			.Should().BeEquivalentTo(["http.requests", "http.latency"]);
	}

	[Fact]
	public async Task WhereIsMonotonic_NullableBool_TranslatesToSql()
	{
		(await NamesAsync("metrics | where IsMonotonic == true")).Should().BeEquivalentTo(["http.requests"]);
	}

	[Fact]
	public async Task AttributesBareFallbackAndPath_TranslateToJsonExtract()
	{
		(await NamesAsync("metrics | where host == 'us-1'"))
			.Should().BeEquivalentTo(["http.requests", "http.latency"]);
		(await NamesAsync("metrics | where Properties.host == 'eu-1'")).Should().BeEquivalentTo(["cpu.usage"]);
	}

	[Fact]
	public async Task TypedAttributeConversion_ComparesNumerically_InSql()
	{
		(await NamesAsync("metrics | where toint(Properties.status_code) >= 500")).Should().BeEquivalentTo(["cpu.usage"]);
	}

	[Fact]
	public async Task SummarizeCountByTypeName_OverSqlSource()
	{
		var code = KustoCode.Parse("metrics | summarize Cnt = count() by TypeName");
		var result = KqlTransformer.ExecuteMetrics(_logDb.MetricPoints, code);
		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);
		var by = rows.ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		by["Gauge"].Should().Be(2);
		by["Sum"].Should().Be(1);
		by["Histogram"].Should().Be(1);
	}

	[Fact]
	public async Task SummarizeSumOverValue_OverSqlSource()
	{
		var code = KustoCode.Parse("metrics | where TypeName == 'Gauge' | summarize Total = sum(Value)");
		var result = KqlTransformer.ExecuteMetrics(_logDb.MetricPoints, code);
		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);
		rows.Should().ContainSingle();
		((double?)rows[0][0]).Should().BeApproximately(1.3, 1e-9);
	}

	// --- PropertiesJson identity split, metrics twin: the streamed metric row names its JSON bag
	// "PropertiesJson" (carrying AttributesJson), so the pre-split context must bind the same bare name to
	// the same value instead of falling back to an Attributes["PropertiesJson"] NULL lookup. ---

	[Fact]
	public async Task PropertiesJsonColumn_SameMeaningPreAndPostSplit()
	{
		// pre-split: a real column filter over the raw attributes JSON, translated to SQL.
		(await NamesAsync("metrics | where PropertiesJson contains 'status_code'"))
			.Should().BeEquivalentTo(["cpu.usage", "cpu.usage"]);
		// the SAME predicate post-split must agree.
		var code = KustoCode.Parse("metrics | extend one = 1 | where PropertiesJson contains 'status_code' | project MetricName");
		var result = KqlTransformer.ExecuteMetrics(_logDb.MetricPoints, code);
		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);
		rows.Select(r => r[0]).Should().BeEquivalentTo(["cpu.usage", "cpu.usage"]);
	}
}
