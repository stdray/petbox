using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using PetBox.Log.Core.Tracing;

namespace PetBox.Tests.Kql;

// SQLite pushdown parity for the `spans` root: the pre-split where/order/top runs as real SQLite SQL
// against a real Spans table (mirroring SqliteKqlIntegrationTests for events). Span-specific mappings
// exercised here: Kind/Status ints, Name/TraceId strings, Start as an epoch-ms-derived datetime column,
// Duration as a computed tick count, and attributes via json_extract over AttributesJson.
public sealed class SpanSqliteKqlIntegrationTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly string _dbPath;
	LogDb _logDb = null!;

	public SpanSqliteKqlIntegrationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-kql-span-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "test.db");
	}

	public async Task InitializeAsync()
	{
		var connStr = $"Data Source={_dbPath};Cache=Shared";
		LogSchema.Ensure(connStr); // creates LogEntries + Spans (idempotent)
		_logDb = new LogDb(LogDb.CreateOptions(connStr));
		await _logDb.Spans.BulkCopyAsync(Seed);
	}

	public Task DisposeAsync()
	{
		_logDb?.Dispose();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	static readonly DateTime Base = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	static SpanRecord Span(
		string spanId, string traceId, string name, SpanKind kind, int startSec, int durMs,
		SpanStatusCode status, string attrs = "{}") => new()
		{
			SpanId = spanId,
			TraceId = traceId,
			Name = name,
			Kind = (int)kind,
			StartUnixNs = new DateTimeOffset(Base.AddSeconds(startSec), TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L,
			EndUnixNs = (new DateTimeOffset(Base.AddSeconds(startSec), TimeSpan.Zero).ToUnixTimeMilliseconds() + durMs) * 1_000_000L,
			StatusCode = (int)status,
			AttributesJson = attrs,
		};

	static readonly SpanRecord[] Seed =
	[
		Span("s1", "t1", "GET /a", SpanKind.Server, 0, 100, SpanStatusCode.Ok, """{"peer":"eu","status_code":200}"""),
		Span("s2", "t1", "db.query", SpanKind.Client, 1, 400, SpanStatusCode.Error, """{"peer":"eu","status_code":500}"""),
		Span("s3", "t2", "GET /b", SpanKind.Server, 2, 50, SpanStatusCode.Ok, """{"peer":"us","status_code":200}"""),
		Span("s4", "t2", "cache.get", SpanKind.Internal, 3, 20, SpanStatusCode.Unset, """{"peer":"us"}"""),
	];

	async Task<IReadOnlyList<string>> IdsAsync(string kql)
	{
		var code = KustoCode.Parse(kql);
		var result = KqlTransformer.ExecuteSpans(_logDb.Spans, code);
		var ids = new List<string>();
		await foreach (var row in result.Rows)
			ids.Add((string)row[0]!); // SpanId is column 0
		return ids;
	}

	[Fact]
	public async Task WhereKind_TranslatesToSql()
	{
		(await IdsAsync("spans | where Kind == 1")).Should().BeEquivalentTo(["s1", "s3"]);
	}

	[Fact]
	public async Task WhereName_Equality_TranslatesToSql()
	{
		(await IdsAsync("spans | where Name == 'db.query'")).Should().BeEquivalentTo(["s2"]);
	}

	[Fact]
	public async Task WhereStatus_TranslatesToSql()
	{
		(await IdsAsync("spans | where Status == 2")).Should().BeEquivalentTo(["s2"]);
	}

	[Fact]
	public async Task WhereStartDatetime_TranslatesToSql()
	{
		(await IdsAsync("spans | where Start >= datetime(2026-04-19T10:00:02Z)"))
			.Should().BeEquivalentTo(["s3", "s4"]);
	}

	[Fact]
	public async Task WhereDurationTimespan_TranslatesToSqlArithmetic()
	{
		// Duration = (EndUnixNs - StartUnixNs)/100 ticks, compared against the literal's .Ticks — all in SQL.
		(await IdsAsync("spans | where Duration > 200ms")).Should().BeEquivalentTo(["s2"]);
		(await IdsAsync("spans | where Duration <= 100ms")).Should().BeEquivalentTo(["s1", "s3", "s4"]);
	}

	[Fact]
	public async Task WhereTraceId_TranslatesToSql()
	{
		(await IdsAsync("spans | where TraceId == 't1'")).Should().BeEquivalentTo(["s1", "s2"]);
	}

	[Fact]
	public async Task AttributesBareFallbackAndPath_TranslateToJsonExtract()
	{
		(await IdsAsync("spans | where peer == 'us'")).Should().BeEquivalentTo(["s3", "s4"]);
		(await IdsAsync("spans | where Properties.peer == 'eu'")).Should().BeEquivalentTo(["s1", "s2"]);
	}

	[Fact]
	public async Task TypedAttributeConversion_ComparesNumerically_InSql()
	{
		(await IdsAsync("spans | where toint(Properties.status_code) >= 500"))
			.Should().BeEquivalentTo(["s2"]);
	}

	[Fact]
	public async Task Top_ByStart_TranslatesToSqlOrderByLimit()
	{
		(await IdsAsync("spans | top 2 by Start desc")).Should().ContainInOrder("s4", "s3");
	}

	[Fact]
	public async Task WhereThenTop_ComposesSqlFilterAndLimit()
	{
		(await IdsAsync("spans | where Kind == 1 | top 1 by Start asc")).Should().ContainInOrder("s1");
	}

	[Fact]
	public async Task SummarizeCountByKindName_OverSqlSource()
	{
		var code = KustoCode.Parse("spans | summarize Cnt = count() by KindName");
		var result = KqlTransformer.ExecuteSpans(_logDb.Spans, code);
		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);
		var by = rows.ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		by["Server"].Should().Be(2);
		by["Client"].Should().Be(1);
		by["Internal"].Should().Be(1);
	}

	[Fact]
	public async Task SummarizeByAttribute_GroupsViaJsonExtract()
	{
		var code = KustoCode.Parse("spans | summarize count() by Properties.peer");
		var result = KqlTransformer.ExecuteSpans(_logDb.Spans, code);
		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Properties.peer", "count_");
		var by = rows.Where(r => r[0] is string).ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		by["eu"].Should().Be(2);
		by["us"].Should().Be(2);
	}

	// --- spans-review fix 1: KindName/StatusName are computed CASE columns — a pre-split where/order
	// over them must TRANSLATE to SQLite (a raw Span*Names.ToName call used to die at enumeration) ---

	[Fact]
	public async Task StatusName_Where_TranslatesToSql()
	{
		(await IdsAsync("spans | where StatusName == 'Error'")).Should().BeEquivalentTo(["s2"]);
	}

	[Fact]
	public async Task KindName_Where_TranslatesToSql()
	{
		(await IdsAsync("spans | where KindName == 'Server'")).Should().BeEquivalentTo(["s1", "s3"]);
	}

	[Fact]
	public async Task KindName_OrderBy_TranslatesToSql()
	{
		// Alphabetical: Client(s2), Internal(s4), Server(s1,s3).
		var ids = await IdsAsync("spans | order by KindName asc");
		ids[0].Should().Be("s2");
		ids[1].Should().Be("s4");
	}

	// --- spans-review fix 3: case-insensitive column forms must hit the same special-literal coercion
	// as the canonical spelling (start → Start datetime, duration → Duration timespan) ---

	[Fact]
	public async Task LowercaseStart_WithDatetimeLiteral_TranslatesToSql()
	{
		(await IdsAsync("spans | where start >= datetime(2026-04-19T10:00:02Z)"))
			.Should().BeEquivalentTo(["s3", "s4"]);
	}

	[Fact]
	public async Task LowercaseDuration_WithTimespanLiteral_TranslatesToSql()
	{
		(await IdsAsync("spans | where duration > 200ms")).Should().BeEquivalentTo(["s2"]);
	}

	// --- spans-review fix 4: timespan/datetime literals normalize inside between / in / reversed
	// comparisons on the SQL path, so pipeline position doesn't change semantics ---

	[Fact]
	public async Task DurationBetween_TimespanLiterals_TranslatesToSql()
	{
		(await IdsAsync("spans | where Duration between (50ms .. 100ms)"))
			.Should().BeEquivalentTo(["s1", "s3"]); // inclusive: 100ms and 50ms
	}

	[Fact]
	public async Task DurationIn_TimespanLiterals_TranslatesToSql()
	{
		(await IdsAsync("spans | where Duration in (100ms, 50ms)")).Should().BeEquivalentTo(["s1", "s3"]);
		(await IdsAsync("spans | where Duration !in (100ms, 50ms)")).Should().BeEquivalentTo(["s2", "s4"]);
	}

	[Fact]
	public async Task ReversedDurationComparison_LiteralOnLeft_TranslatesToSql()
	{
		(await IdsAsync("spans | where 200ms < Duration")).Should().BeEquivalentTo(["s2"]);
	}

	[Fact]
	public async Task StartBetween_DatetimeLiterals_TranslatesToSql()
	{
		(await IdsAsync(
			"spans | where Start between (datetime(2026-04-19T10:00:01Z) .. datetime(2026-04-19T10:00:02Z))"))
			.Should().BeEquivalentTo(["s2", "s3"]);
	}

	// --- spans-review fix 5: an out-of-int-range literal against an int column (Kind/Status) is a
	// precise user error, not a raw OverflowException ---

	[Fact]
	public async Task Kind_OutOfRangeLiteral_ThrowsPrecise()
	{
		var act = () => IdsAsync("spans | where Kind == 3000000000");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*out of range*");
	}
}
