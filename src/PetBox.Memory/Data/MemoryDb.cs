using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;

namespace PetBox.Memory.Data;

// linq2db context over a single memory store file (data/memory/{project}/{store}.db).
public sealed class MemoryDb : DataConnection
{
	public MemoryDb(DataOptions<MemoryDb> options) : base(options.Options) { }

	public ITable<MemoryEntry> Entries => this.GetTable<MemoryEntry>();
	public ITable<MemoryFts> MemoryFts => this.GetTable<MemoryFts>();
	public ITable<MemoryVec> MemoryVec => this.GetTable<MemoryVec>();

	public static DataOptions<MemoryDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
