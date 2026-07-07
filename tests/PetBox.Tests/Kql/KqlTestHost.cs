using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;

namespace PetBox.Tests.Kql;

// The KQL execution backends the production engine can target. Only Sqlite is a LIVE log store today;
// DuckDb is wired as a real switch arm (KqlLogHost.Seed) but its dialect (KqlDialect.DuckDbDialect) is
// still a stub, so it is deliberately kept OUT of KqlBackendConfig.Active — the whole test suite runs
// Sqlite-only until a DuckDB wave flips it on.
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
    public static readonly IReadOnlyList<KqlBackend> Active = [KqlBackend.Sqlite];
}

// The SHARED real-backend seed/run harness. Seeds a fresh in-memory log store with a dataset on the
// chosen backend and hands out the genuine linq2db IQueryable<LogEntryRecord> that KqlTransformer.
// Apply/Execute run over — so tests exercise REAL SQL translation, not the EnumerableQuery provider.
// Every KQL test (DualExecutor's KustoLoco differential AND the direct-assert *Tests files) funnels
// through this ONE type; the static ExecuteAsync/Apply helpers cover the two shapes those callers need.
//
// Lifetime: the in-memory DB lives only while the host is not disposed, so a streamed KqlResult MUST be
// fully materialized before the `using` scope ends. The static helpers do that for you (they drain and
// dispose internally); reach for the instance form only when you need the live IQueryable directly.
public sealed class KqlLogHost : IDisposable
{
    // A private shared-cache in-memory database, kept alive by _keepAlive for the host's lifetime. linq2db
    // opens its OWN connections to the same name (re-registering the KQL scalar functions each time via
    // the LogDb interceptor); the shared cache means they all see the seeded rows.
    readonly SqliteConnection _keepAlive;
    readonly LogDb _db;

    KqlLogHost(IReadOnlyList<LogEntryRecord> records)
    {
        var connectionString =
            $"Data Source=file:petbox-kql-{Guid.NewGuid():N}?mode=memory&cache=shared";

        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        CreateSchema(_keepAlive);

        _db = new LogDb(LogDb.CreateOptions(connectionString));
        // KeepIdentity so the dataset's explicit Ids survive (the [Identity] Id would otherwise be
        // re-assigned by AUTOINCREMENT, breaking every Id-based assertion / KustoLoco Id comparison).
        _db.LogEntries.BulkCopy(new BulkCopyOptions { KeepIdentity = true }, records);
    }

    public IQueryable<LogEntryRecord> LogEntries => _db.LogEntries;

    public static KqlLogHost Seed(IReadOnlyList<LogEntryRecord> records, KqlBackend backend) => backend switch
    {
        KqlBackend.Sqlite => new KqlLogHost(records),
        // Real arm, intentionally unreachable while Active excludes DuckDb: the DuckDB log store isn't
        // wired (DuckDbDialect is a scaffold). Flipping it live is a later wave, not a test concern.
        KqlBackend.DuckDb => throw new NotSupportedException(
            "DuckDb is not a live KQL log store yet (DuckDbDialect is a stub); keep it out of KqlBackendConfig.Active."),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
    }

    // The LogEntries schema, matching the production LogSchema shape (and the [Column] mapping on
    // LogEntryRecord) so BulkCopy and every column reference resolve exactly as they do in prod.
    static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE LogEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ServiceKey TEXT NOT NULL,
                TimestampMs INTEGER NOT NULL,
                Level INTEGER NOT NULL,
                Message TEXT NOT NULL,
                MessageTemplate TEXT NOT NULL,
                Exception TEXT,
                PropertiesJson TEXT NOT NULL DEFAULT '{}',
                TemplateHash INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IX_LogEntries_ServiceKey_TimestampMs ON LogEntries(ServiceKey, TimestampMs DESC);
            CREATE INDEX IX_LogEntries_TimestampMs ON LogEntries(TimestampMs DESC);
            CREATE INDEX IX_LogEntries_Level ON LogEntries(Level);
            """;
        cmd.ExecuteNonQuery();
    }
}

// Convenience run helpers over KqlLogHost — the two result shapes the callers need, each managing the
// host lifetime internally so tests stay a one-liner.
public static class KqlTestHost
{
    // Execute a shape-changing (or any) KQL pipeline over `records` on `backend`, fully materialized:
    // the KqlResult columns plus every row drained to a list. Eager pipeline-build errors (unsupported
    // ops) surface synchronously from KqlTransformer.Execute, as they do in production.
    public static async Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> ExecuteAsync(
        IReadOnlyList<LogEntryRecord> records, KustoCode code, KqlBackend backend, TimeProvider? clock = null)
    {
        using var host = KqlLogHost.Seed(records, backend);
        var result = KqlTransformer.Execute(host.LogEntries, code, clock);
        var rows = new List<object?[]>();
        await foreach (var r in result.Rows)
            rows.Add(r);
        return (result.Columns, rows);
    }

    // Apply the events-shaped subset over `records` on `backend`, returning the produced records (the
    // plain-query path: where/order/take with no shape change).
    public static List<LogEntryRecord> Apply(
        IReadOnlyList<LogEntryRecord> records, KustoCode code, KqlBackend backend, TimeProvider? clock = null)
    {
        using var host = KqlLogHost.Seed(records, backend);
        return KqlTransformer.Apply(host.LogEntries, code, clock).ToList();
    }
}
