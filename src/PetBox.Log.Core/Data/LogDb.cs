using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using PetBox.Log.Core.Tracing;

namespace PetBox.Log.Core.Data;

public sealed class LogDb : DataConnection
{
	public LogDb(DataOptions<LogDb> options) : base(options.Options) { }

	public ITable<LogEntryRecord> LogEntries => this.GetTable<LogEntryRecord>();
	public ITable<SpanRecord> Spans => this.GetTable<SpanRecord>();

	public static DataOptions<LogDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
