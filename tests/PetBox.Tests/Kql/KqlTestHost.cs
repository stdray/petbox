using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Metrics;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Tracing;

namespace PetBox.Tests.Kql;

// The KQL execution backends the production engine can target. Only Sqlite is a LIVE log store today;
// DuckDb is now a REAL switch arm (KqlLogHost ctor) with a real dialect (KqlDialect.DuckDb), but it is
// still deliberately kept OUT of KqlBackendConfig.Active — the shared differential suite runs Sqlite-only
// until the flip wave. A dedicated KqlDuckDbSmokeTests targets KqlBackend.DuckDb directly in the meantime.
public enum KqlBackend
{
	Sqlite,
	DuckDb,
}

public static class KqlBackendConfig
{
	// The backends every KQL test runs its production side over. Adding DuckDb here (once its dialect is
	// real) is the ONE switch that turns the whole suite into a multi-backend differential — no per-test
	// change needed; each shared-helper caller already loops this list.
	public static readonly IReadOnlyList<KqlBackend> Active = [KqlBackend.Sqlite, KqlBackend.DuckDb];
}

// The SHARED real-backend seed/run harness. Seeds a fresh in-memory log store with a dataset on the
// chosen backend and hands out the genuine linq2db IQueryable that KqlTransformer.Apply/Execute*/run over
// — so tests exercise REAL SQL translation, not the EnumerableQuery provider. Every KQL test (DualExecutor's
// KustoLoco differential AND the direct-assert *Tests files) funnels through this ONE type; the static
// KqlTestHost helpers cover the result shapes those callers need for events / spans / metrics.
//
// Lifetime: the in-memory DB lives only while the host is not disposed, so a streamed KqlResult MUST be
// fully materialized before the `using` scope ends. The static helpers do that for you (they drain and
// dispose internally); reach for the instance form only when you need the live IQueryable directly.
public sealed class KqlLogHost : IDisposable
{
	// A private shared-cache in-memory database, kept alive by _keepAlive for the host's lifetime. linq2db
	// opens its OWN connections to the same name (re-registering the KQL scalar functions each time via the
	// LogDb interceptor); the shared cache means they all see the seeded rows. LogSchema.Ensure creates ALL
	// THREE tables (LogEntries + Spans + MetricPoints) exactly as production does, so every seeder maps to
	// the real column shape.
	// SQLite keeps a shared-cache in-memory DB alive for the host's lifetime; DuckDb needs no keep-alive —
	// its single LogDb connection IS the (per-connection) `:memory:` database, so it lives as long as _db.
	readonly SqliteConnection? _keepAlive;
	readonly LogDb _db;
	readonly KqlBackend _backend;

	KqlLogHost(KqlBackend backend)
	{
		_backend = backend;
		switch (backend)
		{
			case KqlBackend.Sqlite:
				{
					var connectionString =
						$"Data Source=file:petbox-kql-{Guid.NewGuid():N}?mode=memory&cache=shared";
					_keepAlive = new SqliteConnection(connectionString);
					_keepAlive.Open();
					LogSchema.Ensure(connectionString);
					_db = new LogDb(LogDb.CreateOptions(connectionString));
					break;
				}
			// Real DuckDb store: one LogDb over DuckDB's per-connection `:memory:` DB. linq2db holds the
			// single connection open for the DataConnection's lifetime, so the schema created by
			// LogSchemaDuckDb.Ensure and the rows seeded by BulkCopy(KeepIds) survive to the queries.
			case KqlBackend.DuckDb:
				{
					_db = new LogDb(LogDb.CreateDuckDbOptions("DataSource=:memory:"));
					LogSchemaDuckDb.Ensure(_db);
					break;
				}
			default:
				throw new ArgumentOutOfRangeException(nameof(backend));
		}
	}

	public IQueryable<LogEntryRecord> LogEntries => _db.LogEntries;
	public IQueryable<SpanRecord> Spans => _db.Spans;
	public IQueryable<MetricPointRecord> MetricPoints => _db.MetricPoints;

	// The dialect / translation options this host's backend compiles against — so a test seeding DuckDb
	// runs its production pipeline with Dialect=DuckDb (SQLite hosts stay on the default SQLite dialect).
	public KqlDialect Dialect => _backend == KqlBackend.DuckDb ? KqlDialect.DuckDb : KqlDialect.Sqlite;
	public KqlTranslationOptions Options => new() { Dialect = Dialect };

	// KeepIdentity so a dataset's explicit Ids survive (an [Identity] Id would otherwise be re-assigned by
	// AUTOINCREMENT, breaking every Id-based assertion / KustoLoco Id comparison); harmless for a natural
	// (non-identity) primary key like SpanRecord.SpanId.
	static readonly BulkCopyOptions KeepIds = new() { KeepIdentity = true };

	// events (LogEntries) — the plain KQL surface; used by DualExecutor and the events *Tests files.
	public static KqlLogHost Seed(IReadOnlyList<LogEntryRecord> records, KqlBackend backend)
	{
		var host = new KqlLogHost(backend);
		host._db.LogEntries.BulkCopy(KeepIds, records);
		return host;
	}

	// spans (Spans) — for the `spans` KQL root (KqlTransformer.ExecuteSpans over host.Spans).
	public static KqlLogHost SeedSpans(IReadOnlyList<SpanRecord> records, KqlBackend backend)
	{
		var host = new KqlLogHost(backend);
		host._db.Spans.BulkCopy(KeepIds, records);
		return host;
	}

	// metrics (MetricPoints) — for the `metrics` KQL root (KqlTransformer.ExecuteMetrics over host.MetricPoints).
	public static KqlLogHost SeedMetrics(IReadOnlyList<MetricPointRecord> records, KqlBackend backend)
	{
		var host = new KqlLogHost(backend);
		host._db.MetricPoints.BulkCopy(KeepIds, records);
		return host;
	}

	public void Dispose()
	{
		_db.Dispose();
		_keepAlive?.Dispose();
	}
}

// Convenience run helpers over KqlLogHost — the result shapes the callers need, each managing the host
// lifetime internally so tests stay a one-liner. Events get Execute + Apply; spans/metrics get Execute
// (their root always yields the streamed table shape — there is no events-style Apply for them).
public static class KqlTestHost
{
	// events Execute: run a shape-changing (or any) `events` pipeline over `records`, fully materialized —
	// KqlResult columns plus every row drained. Eager pipeline-build errors (unsupported ops) surface
	// synchronously from KqlTransformer.Execute, as they do in production.
	public static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> ExecuteAsync(
		IReadOnlyList<LogEntryRecord> records, KustoCode code, KqlBackend backend, TimeProvider? clock = null, KqlTranslationOptions? options = null) =>
		DrainAsync(KqlLogHost.Seed(records, backend), h => KqlTransformer.Execute(h.LogEntries, code, clock, ForBackend(h, options)));

	// events Apply: the events-shaped subset over `records` (where/order/take, no shape change), returning
	// the produced records.
	public static List<LogEntryRecord> Apply(
		IReadOnlyList<LogEntryRecord> records, KustoCode code, KqlBackend backend, TimeProvider? clock = null)
	{
		using var host = KqlLogHost.Seed(records, backend);
		return KqlTransformer.Apply(host.LogEntries, code, clock).ToList();
	}

	// spans Execute: run a `spans` pipeline over `records` (KqlTransformer.ExecuteSpans), fully materialized.
	public static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> ExecuteSpansAsync(
		IReadOnlyList<SpanRecord> records, KustoCode code, KqlBackend backend, TimeProvider? clock = null, KqlTranslationOptions? options = null) =>
		DrainAsync(KqlLogHost.SeedSpans(records, backend), h => KqlTransformer.ExecuteSpans(h.Spans, code, clock, ForBackend(h, options)));

	// metrics Execute: run a `metrics` pipeline over `records` (KqlTransformer.ExecuteMetrics), fully materialized.
	public static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> ExecuteMetricsAsync(
		IReadOnlyList<MetricPointRecord> records, KustoCode code, KqlBackend backend, TimeProvider? clock = null, KqlTranslationOptions? options = null) =>
		DrainAsync(KqlLogHost.SeedMetrics(records, backend), h => KqlTransformer.ExecuteMetrics(h.MetricPoints, code, clock, ForBackend(h, options)));

	// Force the translation Dialect to the host's ACTUAL backend so the query compiles for the store it runs
	// on (esp. the mv-expand seam: SQLite json_each vs DuckDB — the one op that reads options.Dialect rather
	// than linq2db's provider-driven [Sql.Expression] arms). Any other caller-supplied knob (DefaultJoinKind)
	// is preserved; only Dialect is pinned, because it is a function of the backend, not a free choice.
	static KqlTranslationOptions ForBackend(KqlLogHost host, KqlTranslationOptions? caller) =>
		new()
		{
			Dialect = host.Dialect,
			DefaultJoinKind = (caller ?? KqlTranslationOptions.Default).DefaultJoinKind,
			DCountMode = (caller ?? KqlTranslationOptions.Default).DCountMode,
		};

	// Builds the KqlResult (eager unsupported-op throws propagate here) then drains every row, disposing the
	// host only after materialization — so the in-memory DB outlives the streamed enumeration.
	static async Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> DrainAsync(
		KqlLogHost host, Func<KqlLogHost, KqlResult> run)
	{
		using (host)
		{
			var result = run(host);
			var rows = new List<object?[]>();
			await foreach (var r in result.Rows)
				rows.Add(r);
			return (result.Columns, rows);
		}
	}
}
