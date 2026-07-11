using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Core.Search;

// The two primitives a REINDEX (full re-backfill of a Class-B index) needs, next to the
// worker/cursor-store they operate on.
//
// Why a reindex exists at all: while a project had no Embed route, every drain pass burned an
// attempt against innocent documents until they were dead-lettered (Dead=1 → the worker SKIPS
// them forever) — AND the cursor sailed on past them, so they are not even in the delta any more.
// Recovering the route fixes NOTHING by itself: the docs are behind the cursor and blacklisted.
// Resurrection therefore needs BOTH halves reset (SearchIndexReset), and once the cursor is back
// at 0 the delta is the WHOLE store — which is where the take-N cap (SearchDeltaCap) comes in.
public static class SearchIndexReset
{
	// Wipes the Class-B state of the NAMED indexes in one file: the dead-letter rows (so a
	// previously-condemned doc is retried) and the version cursor (so the next drain's delta is
	// the whole store again — DeltaAsync(0) returns every active row as `added`).
	//
	// Explicitly ENUMERATED index names, never a LIKE/prefix sweep: `search_cursor` is a shared
	// table (memory holds `vector:{store}` cursors AND the background jobs' own markers; tasks
	// holds the bare board name), so a wildcard delete would rewind a stranger's cursor and make
	// some other job replay its whole history. Idempotent: running it twice is a no-op on the
	// second run (there is nothing left to delete and the cursor is already 0).
	public static async Task<(int DeadCleared, int CursorsReset)> ResetAsync(
		DataConnection db, IReadOnlyList<string> indexes, CancellationToken ct = default)
	{
		if (indexes.Count == 0) return (0, 0);
		var names = indexes.Distinct(StringComparer.Ordinal).ToList();

		await using var tx = await db.BeginTransactionAsync(ct);
		var dead = await db.GetTable<DeadLetterRow>()
			.Where(r => names.Contains(r.IndexName))
			.DeleteAsync(ct);
		var cursors = await db.GetTable<CursorRow>()
			.Where(r => names.Contains(r.IndexName) && r.Version != 0)
			.Set(r => r.Version, 0L)
			.UpdateAsync(ct);
		await tx.CommitAsync(ct);
		return (dead, cursors);
	}

	[Table("search_cursor")]
	sealed class CursorRow
	{
		[Column, PrimaryKey] public string IndexName { get; set; } = string.Empty;
		[Column] public long Version { get; set; }
	}

	[Table("search_deadletter")]
	sealed class DeadLetterRow
	{
		[Column, PrimaryKey(0)] public string IndexName { get; set; } = string.Empty;
		[Column, PrimaryKey(1)] public string Type { get; set; } = string.Empty;
		[Column, PrimaryKey(2)] public string Id { get; set; } = string.Empty;
		[Column] public int Attempts { get; set; }
		[Column] public bool Dead { get; set; }
	}
}

// Take-N over a temporal delta: a source may hand the worker a PREFIX of the changes instead of
// all of them, and report the prefix's watermark as the delta's CurrentVersion. The worker then
// advances the cursor to that watermark, and the next drain continues from there — the backfill
// walks forward in bounded portions with no extra state.
//
// Why: embedding is ONE sequential HTTP call per document and SearchEnrichmentService runs its
// jobs one after another on a 60s tick. An uncapped post-reset delta (the entire store) would hold
// that single tick for as long as the whole backfill takes, starving the digest/facts/behavior jobs
// behind it. A cap turns "one 20-minute tick" into "N one-portion ticks".
public static class SearchDeltaCap
{
	// Unlimited (the historical behavior) when maxDocs <= 0.
	//
	// The prefix is VERSION-ALIGNED: an upsert batch stamps every row it touched with the SAME
	// version, so cutting inside a version group and advancing the cursor to that version would
	// strand the group's remaining rows FOREVER (the next delta only sees Version > cursor). So the
	// cut is extended through the whole group — a single oversized batch is taken entire (a soft
	// cap by design; correctness beats the ceiling).
	public static (IReadOnlyList<TRow> Rows, long Watermark) Take<TRow>(
		IEnumerable<TRow> changed, long current, int maxDocs)
		where TRow : TemporalRow
	{
		var ordered = changed.OrderBy(r => r.Version).ToList();
		if (maxDocs <= 0 || ordered.Count <= maxDocs) return (ordered, current);
		var watermark = ordered[maxDocs - 1].Version;
		var taken = ordered.TakeWhile(r => r.Version <= watermark).ToList();
		return (taken, watermark);
	}
}
