using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.DuckDB;
using LinqToDB.DataProvider.SQLite;
using PetBox.Log.Core.Metrics;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Tracing;

namespace PetBox.Log.Core.Data;

public sealed class LogDb : DataConnection
{
	public LogDb(DataOptions<LogDb> options) : base(options.Options) { }

	public ITable<LogEntryRecord> LogEntries => this.GetTable<LogEntryRecord>();
	public ITable<SpanRecord> Spans => this.GetTable<SpanRecord>();
	public ITable<MetricPointRecord> MetricPoints => this.GetTable<MetricPointRecord>();

	public static DataOptions<LogDb> CreateOptions(string connectionString) =>
		new(new DataOptions()
			.UseSQLite(connectionString)
			// Loads the vendored sqlean `regexp` extension per connection so its regexp_* SQL functions are
			// available: the KQL translator maps `matches regex`/`extract`/`has`/`has_cs` and the well-formedness
			// gates of the typed conversions (tolong/todouble) to native SQL over them.
			.UseInterceptor(LoadSqleanRegexpInterceptor.Instance));

	// The DuckDb backend options. DuckDB's regexp_*/TRY_CAST/json_* are native (no per-connection extension
	// load like sqlean), so the interceptor only pins `SET TimeZone='UTC'` — required so todatetime
	// (epoch_ms(TRY_CAST(... AS TIMESTAMPTZ))) reads unspecified-offset strings as UTC, matching the
	// SQLite/AssumeUtc contract. No DuckDB.NET type reference here: UseDuckDB is a linq2db-core extension
	// (LinqToDB.DataProvider.DuckDB) and the interceptor runs a plain DbConnection command.
	public static DataOptions<LogDb> CreateDuckDbOptions(string connectionString) =>
		new(new DataOptions()
			.UseDuckDB(connectionString)
			.UseInterceptor(DuckDbInitInterceptor.Instance));
}
