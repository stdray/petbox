using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Search;

namespace PetBox.Tests.Search;

// A REAL SearchPoolCache for tests: HybridCache over a real SqliteDistributedCache over a real
// SQLite file in a per-instance temp dir.
//
// Deliberately not a fake. The pool cache stopped being a dictionary — it now serializes, versions
// and round-trips its payload through a database — and every one of those steps is a place a pool
// can come back subtly different from the one that went in. A stub that hands the same object back
// would assert nothing about the thing that actually ships. It is also the only way the
// "survives a restart" and "foreign payload version" tests can mean anything at all.
public sealed class PoolCacheHarness : IDisposable
{
	readonly string _dir;
	readonly ServiceProvider _services;
	readonly SqliteDistributedCache _storage;

	public SearchPoolCache Cache { get; }

	// The facade itself, for the consumers that use it directly rather than through SearchPoolCache
	// (the board search index).
	public HybridCache Hybrid { get; }

	// The file backing this harness. A second harness over the SAME path is how a process restart is
	// modelled: new objects, same bytes on disk.
	public string DbPath { get; }

	public PoolCacheHarness(string? dbPath = null)
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-poolcache-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		DbPath = dbPath ?? Path.Combine(_dir, "cache.db");

		var connectionString = CacheSchema.ConnectionString(DbPath);
		TestSchema.Cache(connectionString);

		// CleanupInterval zero: no background timer in a test host. The sweep is exercised by
		// calling Cleanup() directly, which is deterministic; a timer would be a race.
		_storage = new SqliteDistributedCache(
			new CacheDbFactory(connectionString), new SqliteDistributedCacheOptions { CleanupInterval = TimeSpan.Zero });

		var services = new ServiceCollection();
		services.AddSingleton<IDistributedCache>(_storage);
		services.AddHybridCache();
		_services = services.BuildServiceProvider();

		Hybrid = _services.GetRequiredService<HybridCache>();
		Cache = new SearchPoolCache(Hybrid);
	}

	// A SECOND harness over the same file — new HybridCache, new SqliteDistributedCache, new
	// SearchPoolCache, same bytes. What a redeploy looks like to the cache.
	public PoolCacheHarness Restart() => new(DbPath);

	public void Dispose()
	{
		_services.Dispose();
		_storage.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}
}
