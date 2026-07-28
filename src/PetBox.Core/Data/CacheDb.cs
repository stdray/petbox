using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace PetBox.Core.Data;

// linq2db context over the disk cache file (data/cache/cache.db). Like deploy.db this is a SINGLE
// fleet-wide file rather than a scope-keyed one, so it is reached through ICacheDbFactory, not
// IScopedDbFactory<T>.
//
// WHY IT IS ITS OWN FILE AND NOT core.db. Everything in here is DERIVED and disposable: the correct
// repair for a corrupt or outdated cache is to delete the file, which must never be an operation
// that can touch business data. It also churns its whole contents on a TTL, which is why it — and
// only it — runs auto_vacuum (see CacheSchema), and it is excluded from backups
// (Backup.ExcludedCacheDirName).
public sealed class CacheDb(DataOptions<CacheDb> options) : DataConnection(options.Options)
{
	public ITable<CacheEntry> Entries => this.GetTable<CacheEntry>();

	// Attribute-mapped, so linq2db uses its own shared default MappingSchema and no per-connection
	// schema is ever built. That is load-bearing rather than incidental: a MappingSchema per
	// connection makes linq2db's MappingAttributesCache grow without bound, which is what produced
	// the ~290 MB production OOM documented on CoreDbFactory. The factory builds these options ONCE
	// and clones them; nothing here may start handing a CacheDb a fresh schema.
	public static DataOptions<CacheDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString).WithDurability(SqliteTier.Derived));
}

// One cached blob. `Key` is the caller's cache key verbatim — the IDistributedCache contract owns
// its shape, and nothing here parses it.
[Table("cache_entries")]
public sealed class CacheEntry
{
	[Column("key"), PrimaryKey, NotNull] public string Key { get; set; } = "";

	[Column("value"), NotNull] public byte[] Value { get; set; } = [];

	// Absolute expiry as UTC ticks, with long.MaxValue meaning "never" (SqliteDistributedCache.NeverTicks).
	// A sentinel rather than NULL so the sweep and the read both compare with a plain `<` and the
	// index stays usable for either.
	[Column("expires_at_ticks"), NotNull] public long ExpiresAtTicks { get; set; }
}
