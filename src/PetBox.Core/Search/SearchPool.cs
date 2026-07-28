using System.Collections.Concurrent;
using PetBox.Core.Contract;

namespace PetBox.Core.Search;

// The RANKED POOL of a relevance selection — the ordered candidate set a query is paged over
// (spec: result-set-pageable). It exists because `q` was never a reason to refuse navigation: the
// pool is FINITE and TOTALLY ORDERED, so a query is a filter like any other, and "page 2" is a
// position inside an order that already exists rather than a second guess at relevance.
//
// WHY THIS TYPE AND NOT JUST A LIST. Two facts have to travel together with the rows, and a bare
// list carries neither:
//   1. POOLLIMIT — how deep ranking was ever allowed to look (the declared rerank candidate
//      budget, default 160 — an assumption, not a value derived from latency). Past it nothing was
//      RANKED, so nothing can be served.
//   2. POOLBOUNDED — whether the candidate union actually HIT that limit. This is the difference
//      between "we ranked everything that matched" and "there are more matches we never looked at",
//      and it is the single most load-bearing bit in this feature: without it a consumer reports a
//      truncated pool as an exhausted search and lies to the user (card requirement 2).
//
// The pool holds ADDRESSES (Hit = type + id + score), never rendered rows. A page re-hydrates its
// own rows from the entity store, so a cached pool can never serve a stale body — only a stale
// ORDER, which the data-version fingerprint refuses outright (see SearchPoolCache).
// `Annotations`, when present, is a per-row label the CONSUMER owns and this type carries opaquely,
// index-aligned with `Ordered`. It exists so a page served from a cached pool reproduces page 1 EXACTLY:
// a consumer may derive a display fact from something that is gone by the time the page is hydrated
// (tasks: whether the row surfaced through a COMMENT doc, which the resolved node address no longer
// says). Without a slot for it, a later page would quietly drop that fact — a small version of the same
// "page 2 is not page 1" defect this whole design exists to prevent. Null when the consumer has none.
public sealed record SearchPool(
	IReadOnlyList<Hit> Ordered,
	int PoolLimit,
	bool PoolBounded,
	SearchRetrievers Retrievers,
	IReadOnlyList<string?>? Annotations = null)
{
	public int Count => Ordered.Count;

	// The consumer label for row `i`, or null. Tolerates a missing/short Annotations list rather than
	// throwing: an absent label must degrade to "no label", never to a failed page.
	public string? AnnotationAt(int i) => Annotations is not null && i < Annotations.Count ? Annotations[i] : null;

	// The identity of THIS ORDER (spec: result-set-pageable) — what a cursor commits to, over and above
	// the query it came from. A pool that is REBUILT rather than reused can come back ranked differently
	// with nothing written and no argument changed: the rerank route recovered or failed between pages,
	// the embed cascade answered on another model, the async vector worker drained the index. None of
	// that moves a data-version stamp, and all of it makes an identity seek land in the wrong list.
	//
	// Computed lazily and cached: a cached pool hands the same string back on every page for free, so the
	// check costs nothing on the path it protects most.
	public string OrderHash => _orderHash ??= KeysetCursor.OrderHashOf(
		Ordered.Select(h => (h.Type + "\x1f" + h.Id, h.Score)));

	string? _orderHash;
}

// WHY THE WALK STOPPED — an explicit three-way answer, and the reason this feature cannot quietly
// mislead (card requirement 2: «граница ВИДИМА и не выглядит как исчерпание»).
//
// The obvious design — "no nextCursor means the end" — is exactly the trap. It makes the honest
// answer ("we ranked the top 160 of some larger match set; the rest was never looked at") and the
// terminal answer ("that was all of it") the SAME wire shape, so a consumer that simply stops when
// the cursor goes missing reports a truncated pool as an exhausted search. Nobody has to be careless
// for that to happen; the shape invites it.
//
// So the stop reason is its own ALWAYS-PRESENT field on a relevance response, and it is an enum
// rather than a bool: absence of a cursor never has to be INTERPRETED, because the response already
// says which of the three things happened. A consumer that ignores the field still cannot mistake
// PoolBoundary for Exhausted — it has to read a value that names one of them.
public enum SearchPoolStop
{
	// Rows remain inside the ranked pool — `nextCursor` is present and paging continues.
	More,

	// The pool was served to its end AND the pool is the complete match set: every entity the
	// filters left was ranked and has now been handed over. This — and only this — means "больше нет".
	Exhausted,

	// The pool was served to its end but the pool itself is a PREFIX of the match set: ranking stopped
	// at PoolLimit and more entities matched behind it. "Дальше не смотрели", NOT "больше нет".
	// A consumer showing this must say the result was cut by ranking depth, and the way forward is to
	// NARROW the query, not to page further — there is no page to reach.
	PoolBoundary,
}

// The materialized-pool CACHE, and the deliberate answer to "how does page 2 keep page 1's order
// without paying for the cross-encoder twice" (card requirements 4 and 5).
//
// WHY A CACHE AT ALL — the alternative was considered and REJECTED. Deterministic recomputation
// (re-run the query per page, fold a data version into the token so a changed basis is refused)
// gets the ORDER right, but it re-runs the RERANK on every page: a few seconds per page against the
// declared candidate budget (RerankCandidateBudget), paid again and again to reproduce a list the
// server had in its hands one request ago. Requirement 5 rules that out. So the ordered pool is
// computed ONCE and kept.
//
// WHAT KEYS IT — the cursor's FINGERPRINT, which already hashes every selection/ordering input and,
// here, the DATA VERSION of the container being searched. One value therefore does both jobs:
//   * cache identity — a different query, sort, filter, ranking mode or data version is a different
//     pool, never a shared one;
//   * cursor validity — KeysetCursor.Decode compares the same fingerprint and THROWS when it moved.
// That composition is what buys requirement 4 for free: when the underlying data changes, the
// fingerprint changes, and an in-flight cursor is refused with an instructive error instead of being
// silently restarted against a new ordering. The refusal is the FEATURE — a walk spliced across two
// orderings is precisely the silent duplicate-and-drop this spec exists to prevent.
//
// THE PRICE, stated plainly (all three are real, none is hidden):
//   * MEMORY — bounded, not free: Capacity pools × up to PoolLimit addresses each. At the defaults
//     (64 × 160 × a (type,id,score) triple) this is well under a megabyte at the current budget, and entries
//     are evicted oldest-first, so the ceiling is a ceiling and not a hope.
//   * INVALIDATION IS COARSE — the data version is per CONTAINER, not per row. Any write to the
//     searched board/store, even one that cannot affect this query's rows, changes the fingerprint
//     and refuses in-flight cursors. On a busy board a walk can be interrupted by an unrelated edit.
//     Accepted deliberately: the failure mode is a loud, restartable error, and the alternative
//     (row-level invalidation) needs an as-of snapshot read the codebase does not have.
//   * COLD PAGE — a pool evicted by TTL or capacity while its data version still holds is
//     RE-MATERIALIZED on the next page, paying one rerank round-trip. Correctness is unaffected (the
//     same query over the same data through the same one model reproduces the same order — which is
//     why the fallback is safe at all); only that one page is slow. This is the honest cost of not
//     making the cache a durability requirement.
public sealed class SearchPoolCache
{
	// How many distinct ranked pools are kept at once. Small on purpose: a pool is only useful for
	// the seconds/minutes a caller spends walking it, and the cost of a miss is one recomputation,
	// not a wrong answer.
	public int Capacity { get; init; } = 64;

	// How long a pool stays valid absent any data change. This is a MEMORY bound, not a correctness
	// one — the data version in the key already handles staleness. Expiry only decides when an
	// abandoned walk stops occupying space.
	public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(10);

	// Injected so tests can expire an entry without sleeping.
	public TimeProvider Clock { get; init; } = TimeProvider.System;

	readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

	// Monotonic insertion stamp, used only to evict the OLDEST entry when Capacity is reached.
	long _seq;

	sealed record Entry(SearchPool Pool, DateTimeOffset ExpiresAt, long Seq);

	// The number of pools currently held. Exposed for tests and diagnostics — a cache whose hit
	// behaviour is a correctness-adjacent claim (requirement 5) must be observable, not asserted.
	public int Count => _entries.Count;

	public bool TryGet(string key, out SearchPool pool)
	{
		if (_entries.TryGetValue(key, out var e))
		{
			if (e.ExpiresAt > Clock.GetUtcNow())
			{
				pool = e.Pool;
				return true;
			}
			// Expired — drop it rather than leaving it to be re-tested on every future lookup.
			_entries.TryRemove(key, out _);
		}

		pool = null!;
		return false;
	}

	public void Put(string key, SearchPool pool)
	{
		var seq = Interlocked.Increment(ref _seq);
		_entries[key] = new Entry(pool, Clock.GetUtcNow() + Ttl, seq);
		Evict();
	}

	// Oldest-first eviction down to Capacity. Deliberately not an LRU: the access pattern here is a
	// forward walk over a pool that is created once and read a handful of times within a short
	// window, so insertion age and recency of use rank entries almost identically — and an LRU would
	// need per-read bookkeeping on the hot path to say the same thing.
	void Evict()
	{
		while (_entries.Count > Capacity)
		{
			var oldest = _entries.OrderBy(kv => kv.Value.Seq).Select(kv => kv.Key).FirstOrDefault();
			if (oldest is null || !_entries.TryRemove(oldest, out _)) return;
		}
	}
}
