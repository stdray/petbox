using LinqToDB;
using LinqToDB.Data;

namespace PetBox.Core.Data;

// Hands out FRESH, caller-owned connections to the disk cache file. The caller disposes.
//
// Same shape and the same two reasons as ICoreDbFactory — read that file for the full history:
// a linq2db DataConnection is NOT thread-safe, so the only safe way to hold one is not to hold one
// (`using var db = factory.Open()` per call); and the DataOptions carrying the mapping are built
// ONCE, because a MappingSchema per connection grows linq2db's MappingAttributesCache without bound
// (~290 MB, a real production OOM).
//
// Deliberately NOT IScopedDbFactory<T>: that one is scope-KEYED (a file per project/workspace, with
// its schema ensured lazily per file). The cache is a SINGLE file whose schema is built up front by
// CacheSchema, exactly like core.db and deploy.db.
public interface ICacheDbFactory
{
	// A fresh, caller-owned connection to the cache db. Never share it across threads; dispose it.
	CacheDb Open();
}

// Registered as a SINGLETON: it holds no connection, only the immutable DataOptions describing how
// to make one.
public sealed class CacheDbFactory : ICacheDbFactory
{
	readonly DataOptions<CacheDb> _options;

	public CacheDbFactory(string connectionString)
		: this(CacheDb.CreateOptions(connectionString)) { }

	// Clones the supplied options, which preserves the provider, the connection string AND the
	// mapping — the same contract CoreDbFactory documents.
	public CacheDbFactory(DataOptions<CacheDb> options) =>
		_options = new DataOptions<CacheDb>(options.Options);

	public CacheDb Open() => new(_options);
}
