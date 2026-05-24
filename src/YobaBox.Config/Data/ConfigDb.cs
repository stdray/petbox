using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using YobaBox.Core.Models;

namespace YobaBox.Config.Data;

public sealed class ConfigDb : DataConnection
{
	public ConfigDb(DataOptions<ConfigDb> options) : base(options.Options) { }

	public ITable<ConfigBinding> Bindings => this.GetTable<ConfigBinding>();

	public static DataOptions<ConfigDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
