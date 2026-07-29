using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Search;

namespace PetBox.Tests.Search;

// The disk cache tier (work/cache-backend-decision): SqliteDistributedCache, its schema, and the
// properties the pool cache above it is entitled to assume.
//
// Three of these cover things that simply could not go wrong while the cache was a dictionary, and
// are the reason the move needed tests of its own rather than only the existing paging suite:
// a payload written by another build, a storage layer that is broken rather than empty, and a
// process that restarted.
public sealed class DiskCacheTests : IDisposable
{
	readonly string _dir;

	// The declared candidate budget (RerankCandidateBudget, default 160). Taken from the type rather
	// than written as a literal: these fixtures used to hardcode 495, the old latency-DERIVED ceiling,
	// and went stale the moment the budget became a declared number.
	static readonly int DeclaredBudget = new RerankCandidateBudget().Candidates();

	public DiskCacheTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-diskcache-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	SqliteDistributedCache NewCache(string file = "cache.db", TimeSpan? cleanup = null)
	{
		var cs = CacheSchema.ConnectionString(Path.Combine(_dir, file));
		TestSchema.Cache(cs);
		return new SqliteDistributedCache(new CacheDbFactory(cs),
			new SqliteDistributedCacheOptions { CleanupInterval = cleanup ?? TimeSpan.Zero });
	}

	// ── the entry survives the process that wrote it ──────────────────────────────────────────────

	[Fact]
	public async Task AnEntry_SurvivesTheCacheObjectThatWroteIt()
	{
		// The whole point of moving off a dictionary: a deploy no longer costs every in-flight walk a
		// rerank. Nothing else in the suite would notice if this silently became an in-memory cache
		// again, because every other test uses one object for the whole test.
		var path = Path.Combine(_dir, "restart.db");
		var cs = CacheSchema.ConnectionString(path);
		TestSchema.Cache(cs);

		using (var writer = new SqliteDistributedCache(new CacheDbFactory(cs), new SqliteDistributedCacheOptions { CleanupInterval = TimeSpan.Zero }))
			await writer.SetAsync("k", [1, 2, 3], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });

		using var reader = new SqliteDistributedCache(new CacheDbFactory(cs), new SqliteDistributedCacheOptions { CleanupInterval = TimeSpan.Zero });
		(await reader.GetAsync("k")).Should().Equal([1, 2, 3]);
	}

	[Fact]
	public async Task APool_SurvivesARestart_SoARedeployDoesNotCostEveryWalkARerank()
	{
		// The same claim one layer up, where it is the one anybody cares about.
		using var first = new PoolCacheHarness();
		var pool = new SearchPool([new Hit("b", "n1", 0.9, "lexical")], DeclaredBudget, false, new SearchRetrievers(true, false, false));
		await first.Cache.GetOrComputeAsync("fp", _ => ValueTask.FromResult(new SearchPoolCache.PoolComputation(pool, true)));

		using var afterRestart = first.Restart();
		var got = await afterRestart.Cache.GetOrComputeAsync("fp", _ => throw new InvalidOperationException(
			"the pool was on disk before the restart — recomputing it means the restart lost it"));

		got.FromCache.Should().BeTrue();
		got.Pool.Ordered.Select(h => h.Id).Should().Equal("n1");
		got.Pool.OrderHash.Should().Be(pool.OrderHash, "a cursor issued before the restart must still seek in this list");
	}

	// ── a foreign payload version reads as a MISS ─────────────────────────────────────────────────

	[Fact]
	public async Task APoolWrittenByAnotherPayloadVersion_IsAMiss_NotGarbageAndNotAnError()
	{
		// The deploy scenario, and the reason the payload carries a version at all. A build that
		// changed the stored shape must not read the previous build's bytes as if they were its own:
		// serving a mis-parsed pool would hand out a WRONG ORDER under a fingerprint asserting it is
		// right, and throwing would turn a routine deploy into failing searches for a whole TTL.
		using var harness = new PoolCacheHarness();

		// Write a payload whose version is not this build's, under the key the cache would look at.
		// v1 is what SearchPoolCache.PayloadFormatVersion currently is; 999 stands in for "some other
		// build's shape".
		var foreign = System.Text.Encoding.UTF8.GetBytes("""{"v":999,"h":[],"pl":1,"pb":false,"r":{"l":true,"s":false,"d":false}}""");
		await StoreRawAsync(harness, "pool:v1:0:fp", foreign);

		var recomputed = new SearchPool([new Hit("b", "fresh", 1.0)], DeclaredBudget, false, new SearchRetrievers(true, false, false));
		var got = await harness.Cache.GetOrComputeAsync("fp",
			_ => ValueTask.FromResult(new SearchPoolCache.PoolComputation(recomputed, true)));

		got.FromCache.Should().BeFalse("a foreign payload version is a MISS — the caller recomputes");
		got.Pool.Ordered.Select(h => h.Id).Should().Equal("fresh");
	}

	[Fact]
	public async Task ACorruptPayload_IsAMiss_NotAnException()
	{
		// The same guarantee for bytes that are not a payload at all — a truncated write, a half-flushed
		// page. HybridCache does not catch anything an L2 throws, so "it deserializes or it throws" would
		// reach the caller as a failed search.
		using var harness = new PoolCacheHarness();
		await StoreRawAsync(harness, "pool:v1:0:fp", [0x7B, 0x22, 0x76]); // `{"v` and then nothing

		var recomputed = new SearchPool([new Hit("b", "fresh", 1.0)], DeclaredBudget, false, new SearchRetrievers(true, false, false));
		var got = await harness.Cache.GetOrComputeAsync("fp",
			_ => ValueTask.FromResult(new SearchPoolCache.PoolComputation(recomputed, true)));

		got.FromCache.Should().BeFalse();
		got.Pool.Ordered.Select(h => h.Id).Should().Equal("fresh");
	}

	// HybridCache stores a byte[] payload under its own key; write straight to the row so the test
	// controls the bytes.
	static async Task StoreRawAsync(PoolCacheHarness harness, string key, byte[] payload)
	{
		using var db = new CacheDbFactory(CacheSchema.ConnectionString(harness.DbPath)).Open();
		await db.InsertOrReplaceAsync(new CacheEntry
		{
			Key = key,
			Value = payload,
			ExpiresAtTicks = DateTimeOffset.UtcNow.AddMinutes(10).Ticks,
		});
	}

	// ── a broken store is a MISS, never an exception ──────────────────────────────────────────────

	[Fact]
	public async Task EveryOperation_AgainstABrokenStore_DegradesInsteadOfThrowing()
	{
		// THE architectural requirement of this tier. HybridCache does NOT swallow L2 exceptions (its
		// only try/catch is SafeReadTagInvalidationAsync), so anything this type throws reaches the
		// caller and breaks the invariant that a storage failure costs a RECOMPUTE, never an error.
		// There is no layer above that can add the guarantee back.
		//
		// "Broken" here is a file that is not a database — the cheapest honest way to make every
		// statement fail, standing in for corruption, a truncated volume or a bad upgrade.
		var path = Path.Combine(_dir, "not-a-database.db");
		await File.WriteAllTextAsync(path, "this is definitely not SQLite");
		using var cache = new SqliteDistributedCache(
			new CacheDbFactory(CacheSchema.ConnectionString(path)),
			new SqliteDistributedCacheOptions { CleanupInterval = TimeSpan.Zero });

		var opts = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };

		(await cache.GetAsync("k")).Should().BeNull("a failed read is a miss");
		cache.Get("k").Should().BeNull();
		await cache.Invoking(c => c.SetAsync("k", [1], opts)).Should().NotThrowAsync();
		cache.Invoking(c => c.Set("k", [1], opts)).Should().NotThrow();
		await cache.Invoking(c => c.RemoveAsync("k")).Should().NotThrowAsync();
		cache.Invoking(c => c.Remove("k")).Should().NotThrow();
		cache.Invoking(c => c.Cleanup()).Should().NotThrow("the sweep runs on a timer thread — an escape there is unobserved");
	}

	[Fact]
	public async Task APoolLookup_OverABrokenStore_StillReturnsAPool()
	{
		// The same thing from the consumer's seat: search must keep working, just without the saving.
		var path = Path.Combine(_dir, "broken-pool.db");
		await File.WriteAllTextAsync(path, "not SQLite either");
		using var storage = new SqliteDistributedCache(
			new CacheDbFactory(CacheSchema.ConnectionString(path)),
			new SqliteDistributedCacheOptions { CleanupInterval = TimeSpan.Zero });

		var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
		Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
			.AddSingleton<IDistributedCache>(services, storage);
		Microsoft.Extensions.DependencyInjection.HybridCacheServiceExtensions.AddHybridCache(services);
		using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
		var cache = new SearchPoolCache(
			Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
				.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(sp));

		var pool = new SearchPool([new Hit("b", "n1", 1.0)], DeclaredBudget, false, new SearchRetrievers(true, false, false));
		var got = await cache.GetOrComputeAsync("fp", _ => ValueTask.FromResult(new SearchPoolCache.PoolComputation(pool, true)));

		got.Pool.Ordered.Select(h => h.Id).Should().Equal(["n1"],
			"a broken cache costs a recomputation, not a failed search");
	}

	// ── expiry, and the file actually shrinking ───────────────────────────────────────────────────

	[Fact]
	public async Task AnExpiredEntry_IsAMiss_NotAStaleHit()
	{
		// A cold page must RE-MATERIALIZE rather than be served an aged pool behind the caller's back.
		// Expiry is a space bound; the data version in the key is what guards staleness.
		//
		// This claim used to be asserted one layer up with an injectable clock. HybridCache has no
		// clock seam, and expiry is implemented HERE — so the assertion moved to where the behaviour
		// lives and became deterministic instead of mocked: an absolute expiry in the past needs no
		// waiting and no fake time.
		using var cache = NewCache();
		await cache.SetAsync("gone", [1], new DistributedCacheEntryOptions
		{
			AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1),
		});

		(await cache.GetAsync("gone")).Should().BeNull();
	}

	[Fact]
	public async Task AnExpiredEntry_IsDroppedOnSight_NotLeftToBeRetestedForever()
	{
		using var cache = NewCache("drop-on-sight.db");
		await cache.SetAsync("gone", [1], new DistributedCacheEntryOptions
		{
			AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1),
		});

		await cache.GetAsync("gone");

		using var db = new CacheDbFactory(CacheSchema.ConnectionString(Path.Combine(_dir, "drop-on-sight.db"))).Open();
		db.Entries.Count(e => e.Key == "gone").Should().Be(0,
			"the read already paid for locating the row — leaving it makes every later lookup re-read a row it discards");
	}

	[Fact]
	public async Task TheSweep_ReturnsTheFreedPagesToTheFilesystem_NotJustToAFreeList()
	{
		// The measured defect that disqualified NeoSmart.Caching.Sqlite: it also ran auto_vacuum, and
		// its file still held 28,176,384 bytes after its 500 rows had gone to 0. auto_vacuum alone only
		// moves freed pages onto a free list — `PRAGMA incremental_vacuum` is what hands them back, and
		// CacheSchema setting the mode BEFORE the migrations is what makes the pragma legal at all.
		var file = Path.Combine(_dir, "shrink.db");
		using var cache = NewCache("shrink.db");

		var chunk = new byte[64 * 1024];
		Random.Shared.NextBytes(chunk);
		for (var i = 0; i < 200; i++)
			await cache.SetAsync($"big-{i}", chunk, new DistributedCacheEntryOptions
			{
				AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1),
			});

		Checkpoint(file);
		var grown = new FileInfo(file).Length;
		grown.Should().BeGreaterThan(4 * 1024 * 1024, "200 x 64 KB has to actually be on disk for the shrink to mean anything");

		cache.Cleanup();
		Checkpoint(file);

		new FileInfo(file).Length.Should().BeLessThan(grown / 2,
			"expired entries must give their pages back — a cache file that only ever grows is the defect this design rejected");
	}

	// Fold the WAL into the main file so FileInfo.Length reflects what the sweep actually did.
	static void Checkpoint(string path)
	{
		using var conn = new SqliteConnection(CacheSchema.ConnectionString(path));
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
		cmd.ExecuteNonQuery();
	}

	// ── the schema is the cache's, and only the cache's ───────────────────────────────────────────

	[Fact]
	public void TheCacheFile_GetsTheCacheSchemaOnly_AndCoreDbNeverGetsIt()
	{
		// Both migration sets now live in the PetBox.Core assembly, so MigrationRunner scans by
		// NAMESPACE. Get that wrong in either direction and the damage is silent: the cache file would
		// be built with the entire core schema, or core.db would grow a cache_entries table. Neither
		// would fail a build or a startup.
		var cachePath = Path.Combine(_dir, "isolation-cache.db");
		CacheSchema.Ensure(CacheSchema.ConnectionString(cachePath));
		var corePath = Path.Combine(_dir, "isolation-core.db");
		TestSchema.Core(SqliteConnectionStrings.ForFile(corePath));

		var cacheTables = TableNames(CacheSchema.ConnectionString(cachePath));
		var coreTables = TableNames(SqliteConnectionStrings.ForFile(corePath));

		cacheTables.Should().Contain("cache_entries");
		cacheTables.Should().NotContain("Projects", "a core table inside the cache file means the namespace filter is not filtering");
		cacheTables.Should().NotContain("ApiKeys");
		coreTables.Should().Contain("Projects", "guards against the filter excluding the Core set as well");
		coreTables.Should().NotContain("cache_entries", "core.db has no use for the cache's table");
	}

	static List<string> TableNames(string connectionString)
	{
		using var conn = new SqliteConnection(connectionString);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
		using var reader = cmd.ExecuteReader();
		var names = new List<string>();
		while (reader.Read()) names.Add(reader.GetString(0));
		return names;
	}

	// ── the board search index round-trips ────────────────────────────────────────────────────────

	[Fact]
	public async Task TheBoardSearchIndex_SurvivesTheRoundTrip_IncludingItsDictionaries()
	{
		// This one used to be handed back BY REFERENCE from an IMemoryCache, so nothing about it had
		// to be serializable. Now it goes through JSON and a BLOB, and its shape is the awkward kind:
		// a record whose members are IReadOnlyDictionary<string, IReadOnlyList<int>>. If those come
		// back empty or null, board search silently stops matching anything — no error, just a search
		// box that finds nothing.
		using var harness = new PoolCacheHarness();
		var hybrid = harness.Hybrid;
		var index = new PetBox.Web.Search.BoardSearchIndex(
			["n1", "n2"],
			new Dictionary<string, IReadOnlyList<int>> { ["deploy"] = [0, 1], ["cache"] = [1] },
			new Dictionary<string, IReadOnlyList<int>> { ["deploy"] = [0] });

		await hybrid.GetOrCreateAsync("board-search-index:v1:p:b:etag", _ => ValueTask.FromResult(index));
		var got = await hybrid.GetOrCreateAsync<PetBox.Web.Search.BoardSearchIndex>(
			"board-search-index:v1:p:b:etag",
			_ => throw new InvalidOperationException("must come from the cache, not be rebuilt"));

		got.Ids.Should().Equal("n1", "n2");
		got.Body.Should().ContainKey("deploy");
		got.Body["deploy"].Should().Equal(0, 1);
		got.Body["cache"].Should().Equal(1);
		got.Title["deploy"].Should().Equal(0);
	}

	// ── the sweep interval is configurable, and its absence changes nothing ───────────────────────

	[Fact]
	public void TheSweepInterval_BindsFromTheCacheSection()
	{
		// appsettings, not the Config module: PetBox never self-configures through ConfigModule
		// (AGENTS.md hard invariant — that module serves EXTERNAL consumers), and a process-level
		// restart-only knob belongs beside the connection string per doc/settings-taxonomy.md.
		var config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Cache:CleanupInterval"] = "00:02:30" })
			.Build();

		var bound = config.GetSection("Cache").Get<SqliteDistributedCacheOptions>();

		bound.Should().NotBeNull();
		bound.CleanupInterval.Should().Be(TimeSpan.FromMinutes(2.5));
	}

	[Fact]
	public void NoCacheSection_LeavesTheCompiledDefaultsExactlyAsTheyWere()
	{
		// The shipped appsettings.json deliberately carries NO `Cache` section, so this is the path
		// production actually takes. It has to land on the same 15 minutes the type declares — a
		// config binding that quietly changes behaviour by existing would be worse than none.
		var config = new ConfigurationBuilder().Build();

		var options = config.GetSection("Cache").Get<SqliteDistributedCacheOptions>()
			?? new SqliteDistributedCacheOptions();

		options.CleanupInterval.Should().Be(TimeSpan.FromMinutes(15));
		options.CleanupInterval.Should().Be(new SqliteDistributedCacheOptions().CleanupInterval,
			"the no-section path and the compiled default are the same thing, not two numbers that agree today");
	}

	[Fact]
	public void TheShippedAppsettings_CarriesNoCacheSection_SoTheDefaultIsTheOneThatShips()
	{
		// Pins the claim the test above rests on. If someone adds a `Cache` section to the shipped
		// appsettings, this fails and they have to decide deliberately whether the default moved.
		var appsettings = Path.Combine(RepoRoot(), "src", "PetBox.Web", "appsettings.json");
		File.Exists(appsettings).Should().BeTrue($"expected the shipped appsettings at {appsettings}");

		using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appsettings));
		doc.RootElement.TryGetProperty("Cache", out _).Should().BeFalse(
			"the shipped config leaves the disk cache on its compiled defaults; a section here means the "
			+ "default now lives in two places");
	}

	static string RepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
		return dir?.FullName ?? throw new InvalidOperationException("repo root (the dir holding AGENTS.md) not found");
	}

	[Fact]
	public void TheCacheConnectionString_BoundsTheWait_BecausePragmaBusyTimeoutDoesNot()
	{
		// Microsoft.Data.Sqlite wraps every command in its OWN managed retry loop keyed to
		// DefaultTimeout, so against a busy writer `PRAGMA busy_timeout` does not bound the wait at
		// all — measured at 200/1000/2000/5000 ms, every one of them blocked ~30-34 s, i.e. the
		// ADO.NET default was the real ceiling throughout. This asserts the knob that actually moves
		// is on the string, and that the string is not carrying Cache=Shared (the SQLITE_LOCKED scar
		// core.db already paid for; the busy handler does not retry it).
		var cs = CacheSchema.ConnectionString(@"C:\tmp\petbox-cache\cache.db");

		using var conn = new SqliteConnection(cs);
		conn.DefaultTimeout.Should().Be(SqliteConnectionStrings.DefaultTimeoutSeconds);
		cs.Should().NotContain("Cache=Shared");
	}
}
