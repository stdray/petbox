using PetBox.Core.Contract;

namespace PetBox.Core.Search;

// The RANKED POOL of a relevance selection — the ordered candidate set a query is paged over
// (spec: result-set-pageable). It exists because `q` was never a reason to refuse navigation: the
// pool is FINITE and TOTALLY ORDERED, so a query is a filter like any other, and "page 2" is a
// position inside an order that already exists rather than a second guess at relevance.
//
// WHY THIS TYPE AND NOT JUST A LIST. Two facts have to travel together with the rows, and a bare
// list carries neither:
//   1. POOLLIMIT — how deep ranking was ever allowed to look (the latency-derived rerank candidate
//      budget, ~495). Past it nothing was RANKED, so nothing can be served.
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
// answer ("we ranked the top 495 of some larger match set; the rest was never looked at") and the
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

// The pool CACHE now lives in SearchPoolCache.cs — it stopped being a dictionary when it moved to
// disk, and its reasoning moved with it.
