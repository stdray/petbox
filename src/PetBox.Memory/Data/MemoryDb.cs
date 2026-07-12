using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;

namespace PetBox.Memory.Data;

// linq2db context over a project's memory file (data/memory/{project}.db) — all of the
// project's stores share it, partitioned by MemoryEntry.Store.
public sealed class MemoryDb : DataConnection
{
	public MemoryDb(DataOptions<MemoryDb> options) : base(options.Options) { }

	public ITable<MemoryEntry> Entries => this.GetTable<MemoryEntry>();

	public ITable<EntryUsage> Usage => this.GetTable<EntryUsage>();

	// Per-delivery events (M011): the cost/fit components behind every entry we handed out.
	public ITable<DeliveryEvent> Deliveries => this.GetTable<DeliveryEvent>();
	// Lexical (search_fts) + vector (search_vec) live behind PetBox.Core.Search indexes, which
	// own their own row mappings — no table props here. See MemoryService search seam.

	public static DataOptions<MemoryDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
