using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace PetBox.Core.Data;

// Behaviour of the disk cache that is NOT about storage. The file, its connection string, its
// pragmas and its schema all belong to the db layer (CacheDb / CacheDbFactory / CacheSchema), so the
// only thing left to configure here is the sweep.
public sealed class SqliteDistributedCacheOptions
{
	// How often expired rows are deleted and the freed pages actually returned to the filesystem.
	// This, together with per-entry TTL, IS the size bound: there is deliberately no row-count or
	// byte quota anywhere. A capacity knob on a cache whose entries all expire is a second eviction
	// policy whose only possible contribution is to disagree with the first.
	//
	// Zero or negative disables the timer (tests drive Cleanup() directly).
	//
	// 15 minutes, matched to SearchPoolCache.DefaultTtl rather than chosen independently. Sweeping
	// much more often than entries expire is pure I/O for nothing; sweeping much less often lets a
	// churned-through cache sit at its high-water mark. Note what this knob does NOT do: it never
	// decides whether an entry is still SERVED. An expired row reads as a miss the moment it is read,
	// whatever the sweep is doing — the sweep only reclaims the disk afterwards.
	public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);
}

// PetBox's IDistributedCache over the cache db. A CONSUMER of ICacheDbFactory — it opens a fresh
// caller-owned connection per operation and does four things: read by key, upsert, delete by key,
// delete expired. Everything about the file itself lives one layer down.
//
// It exists at all because the ready-made alternative was measured and rejected:
// NeoSmart.Caching.Sqlite HUNG under a foreign transaction (8+ minutes, 0 of 400 operations
// completed) and its file never shrank (28,176,384 bytes still held after 500 rows went to 0).
//
// ── FAIL-SOFT IS THIS CLASS'S JOB, NOT ITS CALLERS' ───────────────────────────────────────────────
// Every operation swallows storage failure: a read degrades to a MISS, a write to a silent no-op,
// both with a log line. That is not defensive habit, it is a requirement of what sits ON TOP.
// HybridCache does NOT catch L2 exceptions — its only try/catch is in SafeReadTagInvalidationAsync
// (DefaultHybridCache.L2.cs) — so anything thrown here travels straight through the cache facade
// into the caller and breaks the invariant the whole design rests on: a storage failure must cost a
// RECOMPUTE, never an error. There is nowhere above here to put that guarantee, because the layer
// above is third-party code that declines the job.
//
// ── WHY LINQ2DB, AND WHY NOT CompiledQuery ────────────────────────────────────────────────────────
// linq2db for uniformity with every other database access in this repository. Measured against a raw
// Microsoft.Data.Sqlite implementation of the identical schema on the same stand: warm
// GET+deserialize of a 56 KB payload averaged 157-161 us raw versus 170-176 us here — ~13 us per
// hit, against a miss that costs a MULTI-SECOND cross-encoder pass. p95/p99 and a 12,000-operation
// concurrency run (0 exceptions either way) were indistinguishable.
//
// CompiledQuery is NOT used, on applicability rather than taste. It covers the async READ
// (CompiledQuery.CompileQuery has an ElementAsync branch for [ElementAsync] methods on
// AsyncExtensions, which FirstOrDefaultAsync carries) but NOT async DML: the upsert and both deletes
// throw `InvalidOperationException: Cannot convert async method call to sync.`, because
// CompiledTable<T>.GetInfo puts every expression through ReplaceAsyncWithSync, which looks for the
// sync twin only on the return type's DeclaringType (null for Task<int>) and on Queryable — while
// Delete/InsertOrReplace live on LinqExtensions/DataExtensions and are never found. Verified by
// running it. Since it cannot cover all four operations it covers none: two idioms in one small
// class, for a gain inside measurement noise, is a worse class.
public sealed class SqliteDistributedCache : IDistributedCache, IDisposable
{
	// The "no expiry" sentinel stored in CacheEntry.ExpiresAtTicks.
	private const long NeverTicks = long.MaxValue;

	readonly ICacheDbFactory _factory;
	readonly ILogger<SqliteDistributedCache>? _log;
	readonly Timer? _cleanupTimer;

	public SqliteDistributedCache(ICacheDbFactory factory, SqliteDistributedCacheOptions options,
		ILogger<SqliteDistributedCache>? log = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		_factory = factory ?? throw new ArgumentNullException(nameof(factory));
		_log = log;

		if (options.CleanupInterval > TimeSpan.Zero)
			_cleanupTimer = new Timer(_ => Cleanup(), null, options.CleanupInterval, options.CleanupInterval);
	}

	// ── reads ─────────────────────────────────────────────────────────────────────────────────────

	public byte[]? Get(string key)
	{
		try
		{
			using var db = _factory.Open();
			var row = db.Entries.FirstOrDefault(e => e.Key == key);
			if (row is null) return null;
			if (!IsExpired(row)) return row.Value;
			db.Entries.Where(e => e.Key == key).Delete();
			return null;
		}
		catch (Exception ex)
		{
			LogDegraded(ex, "read", key);
			return null;
		}
	}

	public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
	{
		try
		{
			using var db = _factory.Open();
			var row = await db.Entries.FirstOrDefaultAsync(e => e.Key == key, token);
			if (row is null) return null;
			if (!IsExpired(row)) return row.Value;
			// An expired row is deleted on sight rather than left for the sweep: the read already
			// paid for locating it, and leaving it makes every later lookup of the same key re-read
			// a row it will discard.
			await db.Entries.Where(e => e.Key == key).DeleteAsync(token);
			return null;
		}
		catch (Exception ex)
		{
			LogDegraded(ex, "read", key);
			return null;
		}
	}

	static bool IsExpired(CacheEntry row) =>
		row.ExpiresAtTicks != NeverTicks && row.ExpiresAtTicks < DateTimeOffset.UtcNow.Ticks;

	// ── writes ────────────────────────────────────────────────────────────────────────────────────

	public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
	{
		try
		{
			using var db = _factory.Open();
			db.InsertOrReplace(NewEntry(key, value, options));
		}
		catch (Exception ex)
		{
			LogDegraded(ex, "write", key);
		}
	}

	public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
		CancellationToken token = default)
	{
		try
		{
			using var db = _factory.Open();
			// linq2db's InsertOrReplace on SQLite goes through
			// BuildInsertOrUpdateQueryAsOnConflictUpdateOrNothing — one `INSERT ... ON CONFLICT(key)
			// DO UPDATE SET ...` statement, so two writers of the same key cannot interleave a
			// read-then-write.
			await db.InsertOrReplaceAsync(NewEntry(key, value, options), token: token);
		}
		catch (Exception ex)
		{
			LogDegraded(ex, "write", key);
		}
	}

	static CacheEntry NewEntry(string key, byte[] value, DistributedCacheEntryOptions options) =>
		new() { Key = key, Value = value, ExpiresAtTicks = ExpiryTicksFor(options) };

	// Sliding expiration is not modeled — deliberately. Every consumer in this repository keys its
	// entries by a data version or an ETag, so an entry is superseded by a NEW key rather than
	// refreshed in place; a sliding window would only decide how long dead keys linger, which is what
	// the absolute TTL already decides. Refresh() is therefore a genuine no-op, not a stub.
	static long ExpiryTicksFor(DistributedCacheEntryOptions options)
	{
		if (options is null) return NeverTicks;
		if (options.AbsoluteExpiration is { } absolute) return absolute.ToUniversalTime().Ticks;
		if (options.AbsoluteExpirationRelativeToNow is { } relative) return DateTimeOffset.UtcNow.Add(relative).Ticks;
		return NeverTicks;
	}

	public void Refresh(string key) { }

	public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

	public void Remove(string key)
	{
		try
		{
			using var db = _factory.Open();
			db.Entries.Where(e => e.Key == key).Delete();
		}
		catch (Exception ex)
		{
			LogDegraded(ex, "remove", key);
		}
	}

	public async Task RemoveAsync(string key, CancellationToken token = default)
	{
		try
		{
			using var db = _factory.Open();
			await db.Entries.Where(e => e.Key == key).DeleteAsync(token);
		}
		catch (Exception ex)
		{
			LogDegraded(ex, "remove", key);
		}
	}

	// ── the sweep ─────────────────────────────────────────────────────────────────────────────────

	// Drops expired rows AND returns the freed pages to the filesystem. Both halves are required:
	// auto_vacuum=INCREMENTAL (set once by CacheSchema) only moves freed pages onto a free list.
	// With both, the prototype stand took the file from 27 MB down to 20 KB once entries aged out;
	// the rejected library, which also had auto_vacuum on, never gave a byte back.
	//
	// Public so a test can drive it deterministically instead of waiting for the timer.
	public void Cleanup()
	{
		try
		{
			using var db = _factory.Open();
			var now = DateTimeOffset.UtcNow.Ticks;
			var deleted = db.Entries.Where(e => e.ExpiresAtTicks != NeverTicks && e.ExpiresAtTicks < now).Delete();
			db.Execute(SqlitePragmas.IncrementalVacuumStatement);
			if (deleted > 0 && _log?.IsEnabled(LogLevel.Debug) == true)
				_log.LogDebug("Disk cache swept {Deleted} expired entries.", deleted);
		}
		catch (Exception ex)
		{
			// Catching everything, not just SqliteException: this runs on a Timer thread, where an
			// escaping exception is an unobserved background failure rather than one degraded call.
			// The sweep is best-effort by nature — a missed pass costs disk, and the next tick retries.
			_log?.LogWarning(ex, "Disk cache sweep failed; expired entries stay on disk until the next pass.");
		}
	}

	void LogDegraded(Exception ex, string operation, string key) =>
		_log?.LogWarning(ex,
			"Disk cache {Operation} failed for {Key} — treated as a miss. The caller recomputes; "
			+ "nothing is incorrect, only slower.", operation, key);

	public void Dispose() => _cleanupTimer?.Dispose();
}
