using LinqToDB;
using LinqToDB.Data;
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
			.UseInterceptor(RegisterKqlFunctionsInterceptor.Instance)
			// Infra-only: loads the vendored sqlean `regexp` extension per connection so its regexp_* SQL
			// functions are AVAILABLE. The KQL translator does not use them yet — the .NET UDFs above stay
			// the live regex path; the mapping swap is a later task.
			.UseInterceptor(LoadSqleanRegexpInterceptor.Instance));
}
