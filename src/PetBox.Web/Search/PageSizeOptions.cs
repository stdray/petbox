namespace PetBox.Web.Search;

// ui-search-page-position-and-size: the page-size control shared by every UI search/listing
// surface that keyset-pages (MemoryStore, Memory, Sessions). /ui/search's cross-project fan-out
// (CrossScopeTaskSearchService) is deliberately NOT included — it has no single ranked pool to
// page at all (see that class's own header: "true keyset paging across an arbitrary number of
// projects is a separate, larger design"), so a page-size control there would promise a
// per-project depth knob this fan-out does not have.
//
// One dropdown vocabulary everywhere so a user's "how many at once" expectation transfers
// between pages, and one clamp so a hand-edited/stale `?size=` query value degrades to the
// default instead of erroring — the same tolerance BoardSortKeys/SessionSortKeys already give a
// bad `sortBy`.
//
// `limit`/page size is deliberately NOT part of any KeysetCursor fingerprint
// (KeysetCursor.cs:38-40 — it changes the page, never the sequence), so changing it does not
// itself invalidate an in-progress cursor. The UI still starts the walk over on a size change
// (spec result-set-pageable: a "rows N-M" position counter is meaningless against a different
// page shape) — enforced structurally by putting the size control in the FILTER form (a plain
// GET with no cursor/pos hidden fields), never in the pagination Next link (which always carries
// the CURRENT size forward via this same query parameter).
public static class PageSizeOptions
{
	public const int Default = 40;
	public static readonly IReadOnlyList<int> Allowed = [10, 20, 40, 100];

	// A missing/unrecognized `?size=` value degrades to Default rather than erroring.
	public static int Resolve(int? requested) => requested.HasValue && Allowed.Contains(requested.Value) ? requested.Value : Default;
}
