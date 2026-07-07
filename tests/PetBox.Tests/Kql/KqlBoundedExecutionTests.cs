using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Tracing;

namespace PetBox.Tests.Kql;

// Bounded execution (spec log-query-response-cap + log-query-memory-bound): the log_query
// service caps every response arm — a query with no explicit take/top gets KqlLimits.DefaultTake,
// one with an explicit limit is bounded by MaxTake — and flags the cut; the in-memory post-split
// operators refuse to buffer more rows than the scan cap (the JoinBuildSideCap sibling).
public sealed class KqlBoundedExecutionTests : IAsyncLifetime
{
	readonly string _tempDir;
	LogDb _logDb = null!;
	LogQueryService _service = null!;

	const int SeededEvents = KqlLimits.DefaultTake + 100;

	public KqlBoundedExecutionTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-kql-cap-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
	}

	public async Task InitializeAsync()
	{
		var connStr = $"Data Source={Path.Combine(_tempDir, "test.db")};Cache=Shared";
		LogSchema.Ensure(connStr); // LogEntries + Spans
		_logDb = new LogDb(LogDb.CreateOptions(connStr));
		await _logDb.LogEntries.BulkCopyAsync(Enumerable.Range(1, SeededEvents).Select(i => Rec(i)));
		await _logDb.Spans.BulkCopyAsync(Enumerable.Range(1, 3).Select(i => Span($"s{i}")));
		_service = new LogQueryService(new FakeStore(_logDb));
	}

	public Task DisposeAsync()
	{
		_logDb?.Dispose();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
		return Task.CompletedTask;
	}

	static readonly DateTime Ts = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	static LogEntryRecord Rec(long id, string msg = "m") => new()
	{
		Id = id,
		ServiceKey = "svc",
		TimestampMs = new DateTimeOffset(Ts, TimeSpan.Zero).ToUnixTimeMilliseconds() + id,
		Level = (int)LogLevel.Information,
		Message = $"{msg}{id}",
		MessageTemplate = msg,
		PropertiesJson = "{}",
	};

	static SpanRecord Span(string id) => new()
	{
		SpanId = id,
		TraceId = "t1",
		Name = "op",
		Kind = (int)SpanKind.Internal,
		StartUnixNs = new DateTimeOffset(Ts, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L,
		EndUnixNs = (new DateTimeOffset(Ts, TimeSpan.Zero).ToUnixTimeMilliseconds() + 10) * 1_000_000L,
		StatusCode = (int)SpanStatusCode.Ok,
		AttributesJson = "{}",
	};

	sealed class FakeStore(LogDb db) : ILogStore
	{
		public LogDb GetContext(string projectKey, string logName) => db;
		public LogDb NewContext(string projectKey, string logName) => db;
		public Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default) => Task.FromResult(true);
		public Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<LogMeta> CreateAsync(string projectKey, string logName, string? description, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default) => throw new NotSupportedException();
	}

	async Task<LogQueryResult> RunAsync(string kql) => await _service.QueryAsync("p", "default", kql);

	static async Task<int> CountRowsAsync(KqlResult result)
	{
		var n = 0;
		await foreach (var _ in result.Rows) n++;
		return n;
	}

	// ---- response cap: the events (materialized) arm ----

	[Fact]
	public async Task BareEvents_IsCappedAtDefaultTake_AndFlagged()
	{
		var result = (LogQueryResult.Events)await RunAsync("events");
		result.Items.Should().HaveCount(KqlLimits.DefaultTake);
		result.Truncated.Should().BeTrue();
	}

	[Fact]
	public async Task ExplicitTake_BelowDefault_IsExactAndNotFlagged()
	{
		var result = (LogQueryResult.Events)await RunAsync("events | take 5");
		result.Items.Should().HaveCount(5);
		result.Truncated.Should().BeFalse();
	}

	[Fact]
	public async Task ExplicitTake_AboveDefault_IsHonoredUpToMaxTake()
	{
		// An explicit take lifts the bound from DefaultTake to MaxTake — the seeded excess proves
		// the default is NOT applied on top of the explicit limit.
		var result = (LogQueryResult.Events)await RunAsync($"events | take {SeededEvents}");
		result.Items.Should().HaveCount(SeededEvents);
		result.Truncated.Should().BeFalse();
	}

	// ---- response cap: the table (streamed) arm ----

	[Fact]
	public async Task ShapeChanged_NoExplicitTake_IsCappedAtDefaultTake_AndFlagged()
	{
		var table = (LogQueryResult.Table)await RunAsync("events | project Message");
		(await CountRowsAsync(table.Result)).Should().Be(KqlLimits.DefaultTake);
		// The signal is final only after enumeration.
		table.Truncation.Truncated.Should().BeTrue();
	}

	[Fact]
	public async Task ShapeChanged_WithExplicitTake_NotFlagged()
	{
		var table = (LogQueryResult.Table)await RunAsync("events | project Message | take 7");
		(await CountRowsAsync(table.Result)).Should().Be(7);
		table.Truncation.Truncated.Should().BeFalse();
	}

	[Fact]
	public async Task Spans_UnderTheCap_NotFlagged()
	{
		var table = (LogQueryResult.Table)await RunAsync("spans");
		(await CountRowsAsync(table.Result)).Should().Be(3);
		table.Truncation.Truncated.Should().BeFalse();
	}

	// ---- the explicit-limit detector ----

	[Theory]
	[InlineData("events | take 5", true)]
	[InlineData("events | limit 5", true)]
	[InlineData("events | top 3 by Timestamp desc", true)]
	[InlineData("events | where Level == 4", false)]
	[InlineData("events | summarize count() by ServiceKey", false)]
	// A take INSIDE a join subquery bounds the build side, not the output.
	[InlineData("events | join kind=inner (events | take 5) on Id", false)]
	public void HasExplicitRowLimit_SeesOnlyTopLevelLimits(string kql, bool expected)
	{
		KqlTransformer.HasExplicitRowLimit(KustoCode.Parse(kql)).Should().Be(expected);
	}

	// ---- scan caps: the in-memory buffers refuse to grow past the cap (teaching error, not OOM) ----

	static readonly IReadOnlyList<LogEntryRecord> ManyRows =
		Enumerable.Range(1, 20).Select(i => Rec(i)).ToList();

	static Func<Task> Enumerating(string kql) =>
		() => KqlTestHost.ExecuteAsync(ManyRows, KustoCode.Parse(kql), KqlBackend.Sqlite);

	// NOTE (kql-single-path-impl): ops migrated to SQL no longer buffer in memory, so the InMemoryBufferCap
	// is MOOT for them (result unchanged; the final row count is still bounded by KqlLimits at the response
	// boundary). Progressively removed as ops migrated: `distinct` (SQL DISTINCT), `extend | order by` (SQL
	// ORDER BY), `summarize count() by Message` and no-`by` `dcount` (SQL GROUP BY / bare aggregate). The
	// in-memory GuardBufferCap MECHANISM still guards the FALLBACK path (an op not yet SQL-translatable) — a
	// `summarize min(<string>) by …` forces that fallback (string min/max stays in-memory: linq2db can't
	// carry an ordinal comparer), so the distinct-groups cap is still exercised here.
	[Theory]
	[InlineData("events | summarize m = min(Message) by Message", "summarize (distinct groups)")]
	public async Task InMemoryBuffer_ExceedingScanCap_ThrowsTeachingError(string kql, string opName)
	{
		KqlTransformer.InMemoryBufferCapOverride = 10;
		try
		{
			var act = Enumerating(kql);
			(await act.Should().ThrowAsync<UnsupportedKqlException>())
				.WithMessage($"*{opName} buffered more than 10 rows*narrow the query*");
		}
		finally
		{
			KqlTransformer.InMemoryBufferCapOverride = null;
		}
	}

	[Fact]
	public async Task InMemoryBuffer_WithinScanCap_Succeeds()
	{
		KqlTransformer.InMemoryBufferCapOverride = 100;
		try
		{
			var act = Enumerating("events | extend one = 1 | order by Message | summarize dcount(Message)");
			await act.Should().NotThrowAsync();
		}
		finally
		{
			KqlTransformer.InMemoryBufferCapOverride = null;
		}
	}
}
