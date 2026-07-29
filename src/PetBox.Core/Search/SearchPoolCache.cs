using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace PetBox.Core.Search;

// The materialized-pool CACHE, and the deliberate answer to "how does page 2 keep page 1's order
// without paying for the cross-encoder twice" (spec: result-set-pageable, card requirements 4 and 5).
//
// WHY A CACHE AT ALL — the alternative was considered and REJECTED. Deterministic recomputation
// (re-run the query per page, fold a data version into the token so a changed basis is refused) gets
// the ORDER right, but it re-runs the RERANK on every page: a few seconds per page against the declared
// candidate budget (RerankCandidateBudget), paid again and again to reproduce a list the server had in
// its hands one request ago. Requirement 5 rules that out. So the ordered pool is computed ONCE and kept.
//
// WHAT KEYS IT — the cursor's FINGERPRINT, which already hashes every selection/ordering input and,
// here, the DATA VERSION of the container being searched. One value therefore does both jobs:
//   * cache identity — a different query, sort, filter, ranking mode or data version is a different
//     pool, never a shared one;
//   * cursor validity — KeysetCursor.Decode compares the same fingerprint and THROWS when it moved.
// That composition buys requirement 4 for free: when the underlying data changes the fingerprint
// changes, and an in-flight cursor is refused with an instructive error instead of being silently
// restarted against a new ordering. The refusal is the FEATURE — a walk spliced across two orderings
// is precisely the silent duplicate-and-drop this spec exists to prevent. NOTHING about the key
// changed when this moved to disk, and nothing may: the fingerprint is doing two jobs at once.
//
// ── WHAT MOVING TO DISK CHANGED, AND WHAT IT DID NOT ─────────────────────────────────────────────
// Pools used to live in a process-local dictionary with a CAPACITY of 64 and oldest-first eviction.
// They now live in SqliteDistributedCache behind HybridCache. Three consequences, stated plainly:
//
//   * THERE IS NO CAPACITY BOUND ANY MORE, on purpose. The bound is TTL plus the cache's background
//     sweep, which reclaims the file (measured: 27 MB back down to 20 KB once entries aged out). A
//     count-based ceiling on top of that would be a second eviction policy whose only possible
//     contribution is to disagree with the first — and the old one's real job was to bound RAM,
//     which is no longer where pools are.
//   * A POOL NOW SURVIVES A PROCESS RESTART. That is a straightforward gain (a deploy no longer
//     costs every in-flight walk a rerank) and it is why `Invalidate` exists at all — see there.
//   * L1 IS OFF (DisableLocalCacheRead | DisableLocalCacheWrite). Deliberate: keeping a second copy
//     of every pool in memory would reintroduce exactly the unbounded RAM the move was meant to
//     shed. What HybridCache is here for is the OTHER thing it does — single-flighting concurrent
//     misses, so fifty simultaneous cold pages run the cross-encoder once rather than fifty times.
//
// THE REMAINING PRICE, unchanged by the move:
//   * INVALIDATION IS COARSE — the data version is per CONTAINER, not per row. Any write to the
//     searched board/store, even one that cannot affect this query's rows, changes the fingerprint
//     and refuses in-flight cursors. Accepted deliberately: the failure mode is a loud, restartable
//     error, and the alternative (row-level invalidation) needs an as-of snapshot read the codebase
//     does not have.
//   * COLD PAGE — a pool dropped by TTL while its data version still holds is RE-MATERIALIZED on the
//     next page, paying one rerank round-trip. Correctness is unaffected (the same query over the
//     same data through the same one model reproduces the same order — which is why the fallback is
//     safe at all); only that one page is slow.
public sealed class SearchPoolCache
{
	// The stored payload's SHAPE version, exactly the role KeysetCursor.FormatVersion plays for a
	// cursor token. Bump it whenever the wire shape below changes.
	//
	// A pool written by a DIFFERENT version must read as a plain MISS: not as garbage (which would
	// serve a wrong order under a fingerprint that says it is right), and not as an error (which
	// would turn a routine deploy into failing searches for the length of one TTL). Both halves of
	// that are enforced below — the version rides in the key, so an old entry is never even fetched,
	// AND it rides in the payload, so anything that does arrive is checked before it is trusted.
	// The key half is the cheap one; the payload half is the one that still holds if the key scheme
	// is ever wrong.
	const int PayloadFormatVersion = 1;

	// 15 minutes — THE knob that decides how long a caller may take before page 2 stops being cheap.
	//
	// Raised from 10 when the cache moved to disk, for the reason the move enables: the pools are no
	// longer competing for process memory, so the only thing a longer window costs is disk that the
	// sweep reclaims anyway. The case it is sized for is an AGENT rather than a human — a walk can sit
	// idle for many minutes while the caller reasons about page 1, and every expiry inside that window
	// bills the next page a multi-second cross-encoder pass for nothing.
	//
	// Correctness does not depend on this number in either direction: the data version in the key
	// refuses a stale ordering however long the entry lives, and a cold page re-materializes
	// deterministically. It trades disk for latency and nothing else.
	private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

	// How long a pool stays valid absent any data change. This is a SPACE bound, not a correctness
	// one — the data version in the key already handles staleness. Expiry only decides when an
	// abandoned walk stops occupying disk.
	//
	// A CONSTRUCTOR parameter rather than an `init` property, which is what it used to be: the entry
	// options are built from it in the constructor, and an `init` setter runs AFTER the constructor
	// body — so `new SearchPoolCache(h) { Ttl = ... }` would have silently kept the default while
	// reading back the value the caller asked for. A property that lies about what took effect is
	// worse than one that cannot be set.
	private TimeSpan Ttl { get; }

	// Null ONLY in the Disabled instance below.
	readonly HybridCache? _cache;
	readonly ILogger<SearchPoolCache>? _log;

	// L1 off in both directions — see the class note. The pool is up to ~500 addresses; a hot board
	// under many concurrent walks would otherwise hold megabytes of duplicate order in RAM for the
	// whole TTL, which is the cost this move exists to remove.
	readonly HybridCacheEntryOptions _entryOptions;

	// Diagnostics, and the successor to the old `Count`. A cache whose hit behaviour is a
	// correctness-adjacent claim (requirement 5: "page 2 runs no second rerank") has to be
	// OBSERVABLE, not asserted — but a disk cache has no cheap "how many pools are live" answer, and
	// asking SQLite to COUNT rows in a file shared with other consumers would not be one either.
	// These count what the old `Count` was really being used to prove: that a pool was stored, and
	// that a second page did not store another.
	public long Stores => Interlocked.Read(ref _stores);
	public long Hits => Interlocked.Read(ref _hits);
	public long Misses => Interlocked.Read(ref _misses);

	long _stores, _hits, _misses;

	// Bumped by Invalidate(); prefixed into every key, so bumping it makes every pool this process
	// previously stored unreachable.
	//
	// WHY IT EXISTS. It is a test seam, and saying so is more honest than dressing it up: several
	// tests need the state "the pool is gone, so this page must REBUILD" in order to exercise the
	// order-hash refusal at all. They used to reach it by overflowing a 64-entry capacity, which no
	// longer exists, and they cannot reach it by restarting the process, because on a disk cache that
	// no longer drops anything. Waiting out a TTL is not a test. There is precedent for the shape —
	// the previous implementation carried an injectable `Clock` for the same reason, and said so.
	int _generation;

	public SearchPoolCache(HybridCache cache, ILogger<SearchPoolCache>? log = null, TimeSpan? ttl = null)
	{
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		_log = log;
		Ttl = ttl ?? DefaultTtl;
		_entryOptions = new HybridCacheEntryOptions
		{
			Expiration = Ttl,
			Flags = HybridCacheEntryFlags.DisableLocalCacheRead | HybridCacheEntryFlags.DisableLocalCacheWrite,
		};
	}

	SearchPoolCache()
	{
		Ttl = DefaultTtl;
		_entryOptions = new HybridCacheEntryOptions();
	}

	// THE EXPLICIT "no pool cache is wired here" instance — every computation runs, nothing is stored.
	//
	// It replaces the old `poolCache ?? new SearchPoolCache()` fallback in MemoryService/TasksService,
	// which was the worst of both worlds: a host that forgot the DI registration silently got a
	// PRIVATE cache on a per-request service, so every page stored a pool nobody would ever read and
	// the hit rate was structurally zero. That is quiet slowness wearing a cache's name. Correctness
	// never depended on the cache — a pool re-materializes deterministically — so the honest fallback
	// is one that admits it caches nothing, in a form that shows up by NAME at the call site, in a
	// debugger, and in a stack trace.
	public static SearchPoolCache Disabled { get; } = new();

	// Makes every pool stored so far unreachable. See `_generation`.
	public void Invalidate() => Interlocked.Increment(ref _generation);

	// What a caller's computation produced: the pool, and whether it is fit to KEEP.
	//
	// `Cacheable` is how "a DEGRADED pool is never stored" survives the move to a get-or-create API.
	// Caching a degraded pool pins a half-answer — plus its now-stale provenance — for the whole TTL,
	// so every repeat of the query keeps hitting an outage that has already healed, turning a
	// self-healing blip into ten minutes of quietly worse results.
	public readonly record struct PoolComputation(SearchPool Pool, bool Cacheable);

	// The pool, and whether it came from storage rather than from `compute`.
	//
	// `FromCache` is what tells a caller which of its two branches to take, and it is NOT the same
	// question as "did I miss": under concurrent misses HybridCache single-flights the computation,
	// so a caller that lost that race never ran its own `compute` and must take the cached branch
	// like any other reader. Deriving this inside the cache — from whether THIS caller's factory
	// actually executed — is the only place that distinction is knowable.
	public readonly record struct PoolLookup(SearchPool Pool, bool FromCache);

	public async ValueTask<PoolLookup> GetOrComputeAsync(
		string key,
		Func<CancellationToken, ValueTask<PoolComputation>> compute,
		CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(compute);

		if (_cache is null)
		{
			Interlocked.Increment(ref _misses);
			var uncached = await compute(ct);
			return new PoolLookup(uncached.Pool, FromCache: false);
		}

		SearchPool? computed = null;
		var cacheable = true;

		var bytes = await _cache.GetOrCreateAsync(
			VersionedKey(key),
			async innerCt =>
			{
				var result = await compute(innerCt);
				computed = result.Pool;
				cacheable = result.Cacheable;
				if (cacheable) Interlocked.Increment(ref _stores);
				return Serialize(result.Pool);
			},
			_entryOptions,
			tags: null,
			ct);

		if (computed is not null)
		{
			Interlocked.Increment(ref _misses);
			if (!cacheable)
			{
				// HybridCache decides its write flags BEFORE the factory runs, so "do not keep this
				// one" cannot be expressed up front — the entry is written and then withdrawn. The
				// window is microseconds and what a reader could catch inside it is the degraded pool
				// they would otherwise have computed themselves, so the property that matters holds:
				// the outage is not pinned for a TTL.
				await _cache.RemoveAsync(VersionedKey(key), ct);
			}
			return new PoolLookup(computed, FromCache: false);
		}

		// This caller did not run the factory: either the entry was already on disk, or another
		// caller's in-flight computation was joined.
		var pool = Deserialize(bytes);
		if (pool is null)
		{
			// A payload we cannot read is a MISS, and the caller has already been handed a pool it
			// cannot use — so recompute rather than return something wrong. This is the path a
			// foreign PayloadFormatVersion takes if it ever reaches deserialization at all.
			Interlocked.Increment(ref _misses);
			var fallback = await compute(ct);
			return new PoolLookup(fallback.Pool, FromCache: false);
		}

		Interlocked.Increment(ref _hits);
		return new PoolLookup(pool, FromCache: true);
	}

	// The version rides in the KEY as well as in the payload: an entry from another format is then
	// never fetched at all, and ages out under its own TTL instead of being read and rejected on
	// every request for the length of one.
	string VersionedKey(string key) =>
		$"pool:v{PayloadFormatVersion}:{Volatile.Read(ref _generation)}:{key}";

	static byte[] Serialize(SearchPool pool) =>
		JsonSerializer.SerializeToUtf8Bytes(
			new PoolPayload(
				PayloadFormatVersion,
				[.. pool.Ordered.Select(h => new HitPayload(h.Type, h.Id, h.Score, h.Retriever))],
				pool.PoolLimit,
				pool.PoolBounded,
				new RetrieversPayload(
					pool.Retrievers.Lexical, pool.Retrievers.Semantic, pool.Retrievers.Degraded,
					pool.Retrievers.DegradedReason, pool.Retrievers.SemanticLag, pool.Retrievers.Ranking),
				pool.Annotations),
			PayloadJson);

	// Returns null for ANY payload this build cannot honour — a foreign version, a truncated blob,
	// something that is not JSON at all. Never throws: the contract this whole cache lives under is
	// that a storage problem costs a recomputation, never an error (see SqliteDistributedCache).
	SearchPool? Deserialize(byte[]? bytes)
	{
		if (bytes is null || bytes.Length == 0) return null;
		try
		{
			var payload = JsonSerializer.Deserialize<PoolPayload>(bytes, PayloadJson);
			if (payload is null) return null;
			if (payload.Version != PayloadFormatVersion)
			{
				if (_log?.IsEnabled(LogLevel.Debug) == true)
					_log.LogDebug(
						"Ignoring a cached search pool written in payload format v{Found} (this build reads v{Expected}) — recomputing.",
						payload.Version, PayloadFormatVersion);
				return null;
			}

			return new SearchPool(
				[.. payload.Hits.Select(h => new Hit(h.Type, h.Id, h.Score, h.Retriever))],
				payload.PoolLimit,
				payload.PoolBounded,
				new SearchRetrievers(
					payload.Retrievers.Lexical, payload.Retrievers.Semantic, payload.Retrievers.Degraded,
					payload.Retrievers.DegradedReason, payload.Retrievers.SemanticLag, payload.Retrievers.Ranking),
				payload.Annotations);
		}
		catch (JsonException ex)
		{
			_log?.LogWarning(ex, "A cached search pool could not be read back — treating it as a miss.");
			return null;
		}
	}

	static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

	// The stored SHAPE, declared here rather than by serializing the domain records directly. Two
	// reasons, and the second is the load-bearing one: short names because this is written and read
	// on a hot path, and — more importantly — a refactor of `Hit` or `SearchRetrievers` must not be
	// able to change the on-disk format by accident. With the shape declared separately, such a
	// change breaks THIS file's compilation, which is where someone can decide whether the version
	// needs bumping.
	sealed record PoolPayload(
		[property: JsonPropertyName("v")] int Version,
		[property: JsonPropertyName("h")] IReadOnlyList<HitPayload> Hits,
		[property: JsonPropertyName("pl")] int PoolLimit,
		[property: JsonPropertyName("pb")] bool PoolBounded,
		[property: JsonPropertyName("r")] RetrieversPayload Retrievers,
		[property: JsonPropertyName("a")] IReadOnlyList<string?>? Annotations);

	sealed record HitPayload(
		[property: JsonPropertyName("t")] string Type,
		[property: JsonPropertyName("i")] string Id,
		[property: JsonPropertyName("s")] double Score,
		[property: JsonPropertyName("r")] string? Retriever);

	sealed record RetrieversPayload(
		[property: JsonPropertyName("l")] bool Lexical,
		[property: JsonPropertyName("s")] bool Semantic,
		[property: JsonPropertyName("d")] bool Degraded,
		[property: JsonPropertyName("dr")] string? DegradedReason,
		[property: JsonPropertyName("sl")] long? SemanticLag,
		[property: JsonPropertyName("rk")] SearchRankingOutcome? Ranking);
}
