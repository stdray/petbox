using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Metrics;

namespace PetBox.Tests.Data;

// Storage-layer smoke test for the MetricPoints table: LogSchema.Ensure must CREATE it on the same
// per-log SQLite file as LogEntries/Spans, and MetricPointRecord must round-trip through BulkCopyAsync
// (the path the future OTLP ingest endpoint uses). Mirrors how the spans KQL integration tests exercise
// the Spans table via LogSchema.Ensure + BulkCopyAsync.
public sealed class MetricPointsSchemaTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly string _dbPath;
	LogDb _logDb = null!;

	public MetricPointsSchemaTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-metrics-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "test.db");
	}

	public Task InitializeAsync()
	{
		var connStr = $"Data Source={_dbPath};Cache=Shared";
		LogSchema.Ensure(connStr); // creates LogEntries + Spans + MetricPoints (idempotent)
		_logDb = new LogDb(LogDb.CreateOptions(connStr));
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		_logDb?.Dispose();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	[Fact]
	public async Task BulkCopy_ThenReadBack_RoundTripsWideColumnsAndJsonTail()
	{
		var points = new[]
		{
			// Gauge — double value arm, wide scalars only.
			new MetricPointRecord
			{
				MetricName = "process.cpu.utilization",
				MetricType = (int)MetricPointType.Gauge,
				Unit = "1",
				TimeUnixNs = 1_700_000_000_000_000_000L,
				ValueDouble = 0.42,
				AttributesJson = """{"host":"a"}""",
			},
			// Sum — long value arm, monotonic + temporality.
			new MetricPointRecord
			{
				MetricName = "http.server.requests",
				MetricType = (int)MetricPointType.Sum,
				TimeUnixNs = 1_700_000_000_000_000_001L,
				StartUnixNs = 1_699_000_000_000_000_000L,
				ValueLong = 9223372036854775807L, // int64 max — exactness must survive
				IsMonotonic = true,
				AggregationTemporality = 2,
				AttributesJson = """{"route":"/x"}""",
			},
			// Histogram — aggregate scalars + JSON tail arrays.
			new MetricPointRecord
			{
				MetricName = "http.server.duration",
				MetricType = (int)MetricPointType.Histogram,
				TimeUnixNs = 1_700_000_000_000_000_002L,
				Count = 3,
				Sum = 12.5,
				Min = 1.0,
				Max = 9.0,
				ExplicitBoundsJson = "[1,5,10]",
				BucketCountsJson = "[0,1,2,0]",
			},
		};

		await _logDb.MetricPoints.BulkCopyAsync(points);

		var all = await _logDb.MetricPoints.Where(p => p.Id > 0).ToListAsync();
		all.Should().HaveCount(3);

		var sum = all.Single(p => p.MetricType == (int)MetricPointType.Sum);
		sum.ValueLong.Should().Be(9223372036854775807L);
		sum.ValueDouble.Should().BeNull();
		sum.IsMonotonic.Should().BeTrue();
		sum.AggregationTemporality.Should().Be(2);

		var gauge = all.Single(p => p.MetricName == "process.cpu.utilization");
		gauge.ValueDouble.Should().Be(0.42);
		gauge.ValueLong.Should().BeNull();
		gauge.Unit.Should().Be("1");

		var hist = all.Single(p => p.MetricType == (int)MetricPointType.Histogram);
		hist.Count.Should().Be(3);
		hist.Sum.Should().Be(12.5);
		hist.ExplicitBoundsJson.Should().Be("[1,5,10]");
		hist.BucketCountsJson.Should().Be("[0,1,2,0]");
		hist.ExemplarsJson.Should().BeNull();
	}
}
