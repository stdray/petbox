using PetBox.Core.Contract;
using PetBox.Core.Search;

namespace PetBox.Web.Mcp;

// The response cap shared by tasks_delta / comments_delta / memory_delta (card delta-verbs-response-cap).
//
// A delta from sinceVersion:0 on a big board is hundreds of KB, and an agent harness spills anything past
// its output limit into a file — where a caller that reads `currentVersion` off the head loses the tail for
// good. So the rows (added + updated, by Version) are cut by the shared ResponseBudget and an optional
// `limit`, whichever comes first, and the cut is VERSION-ALIGNED by SearchDeltaCap.Take: a batch stamps one
// version on all its rows and `sinceVersion` is exclusive, so cutting inside a group would strand its rest
// forever; the group is taken whole (a single giant batch is returned entire — correctness beats the cap).
// `currentVersion` becomes the watermark of the kept prefix, so the caller passes it back as `sinceVersion`
// and repeats. `removed` is NOT paged (tiny, idempotent) — the caller repeats it on every page.
public static class McpDeltaPage
{
	public const string Hint =
		"Delta truncated (see truncated/omitted): more changed rows follow. Pass `currentVersion` back as "
		+ "`sinceVersion` and repeat until a response has no `truncated` (or added and updated are empty). "
		+ "`removed` is repeated on every page — apply it idempotently.";

	public sealed record Slice<T>(IReadOnlyList<T> Added, IReadOnlyList<T> Updated, long CurrentVersion, int Omitted);

	public static Slice<T> Cut<T>(
		IReadOnlyList<T> added, IReadOnlyList<T> updated, long current, Func<T, long> versionOf, int? limit, int budgetChars = 30_000)
	{
		var rows = added.Select(r => (Row: r, IsAdded: true)).Concat(updated.Select(r => (Row: r, IsAdded: false)))
			.OrderBy(x => versionOf(x.Row)).ToList();
		if (rows.Count == 0) return new Slice<T>(added, updated, current, 0);

		// How many rows the char budget allows (the same wire-cost measure every *_search verb uses).
		var fits = new ResponseBudget(budgetChars).Take(rows.Select(x => x.Row).ToList()).Rows.Count;
		var maxDocs = limit is > 0 ? Math.Min(limit.Value, fits) : fits;
		if (maxDocs < 1) maxDocs = 1; // the first row alone exceeds the budget: still deliver its whole version group

		var (taken, watermark) = SearchDeltaCap.Take(rows, x => versionOf(x.Row), current, maxDocs);
		if (taken.Count == rows.Count) return new Slice<T>(added, updated, current, 0); // the cut reached the end: complete
		return new Slice<T>(
			taken.Where(x => x.IsAdded).Select(x => x.Row).ToList(),
			taken.Where(x => !x.IsAdded).Select(x => x.Row).ToList(),
			watermark, rows.Count - taken.Count);
	}
}
