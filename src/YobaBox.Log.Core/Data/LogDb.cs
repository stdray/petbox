using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;

namespace YobaBox.Log.Core.Data;

public sealed class LogDb : DataConnection
{
	public LogDb(DataOptions<LogDb> options) : base(options.Options) { }

	public ITable<LogEntryRecord> LogEntries => this.GetTable<LogEntryRecord>();

	public static DataOptions<LogDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
