using LinqToDB;
using LinqToDB.Data;

namespace PetBox.Core.Data.Temporal;

public enum TemporalConflictKind
{
	// The active revision moved past the author's baseline before they submitted.
	Stale,

	// The author edited a node (baseline > 0) that no longer exists.
	Vanished,

	// A concurrent writer closed the baseline row inside our read→write window.
	CloseRace,
}

// One row the caller could not apply because the store moved under its baseline.
public sealed record TemporalConflict(
	string Key,
	TemporalConflictKind Kind,
	long BaselineVersion,
	long? ActiveVersion);

public sealed record TemporalUpsertResult(
	bool Applied,
	long FromVersion,
	long ToVersion,
	int Inserted,
	int Closed,
	IReadOnlyList<TemporalConflict> Conflicts)
{
	public bool HasConflicts => Conflicts.Count > 0;

	internal static TemporalUpsertResult Rejected(long version, IReadOnlyList<TemporalConflict> conflicts) =>
		new(false, version, version, 0, 0, conflicts);

	internal static TemporalUpsertResult NoChanges(long version) =>
		new(true, version, version, 0, 0, []);

	internal static TemporalUpsertResult Committed(long fromVersion, long toVersion, int inserted, int closed) =>
		new(true, fromVersion, toVersion, inserted, closed, []);
}

// Declarative, append-only upsert for a batch of keyed rows.
//
// Each desired row carries the version its author last saw (Version == 0 means
// "I believe this is new"). Optimistic concurrency keys on the AUTHOR's baseline
// version, not on a freshly re-read version, so a change that landed any time
// during the author's think-time is caught. Any conflict aborts the whole batch
// (nothing is written) and the conflicts are returned so the caller can rebase.
//
// The batch is meant to flow through a single-writer SQLite file (one DB per
// scope), which serialises writers and keeps the global Version a clean cursor.
//
// The flow is three steps: read current state → classify each row → apply.
public static class TemporalStore
{
	public static Task<TemporalUpsertResult> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		CancellationToken ct = default)
		where TRow : TemporalRow =>
		UpsertAsync(db, desired, onBeforeApply: null, ct);

	// onBeforeApply is a test-only seam: it runs after classification but before
	// the close+insert transaction, to deterministically exercise the CloseRace
	// branch (a concurrent writer commits inside our read→close window).
	internal static async Task<TemporalUpsertResult> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		Func<Task>? onBeforeApply,
		CancellationToken ct)
		where TRow : TemporalRow
	{
		var table = db.GetTable<TRow>();

		var active = await ActiveByKeyAsync(table, desired, ct);
		var fromVersion = await MaxVersionAsync(table, ct);
		var nextVersion = fromVersion + 1;
		var now = DateTime.UtcNow;

		var batch = Classify(desired, active, nextVersion, now);

		if (batch.Conflicts.Count > 0)
			return TemporalUpsertResult.Rejected(fromVersion, batch.Conflicts);
		if (batch.IsEmpty)
			return TemporalUpsertResult.NoChanges(fromVersion);

		if (onBeforeApply is not null)
			await onBeforeApply();

		return await ApplyAsync(db, table, batch, fromVersion, nextVersion, now, ct);
	}

	// ── 1. read current state ────────────────────────────────────────────────

	static async Task<Dictionary<string, TRow>> ActiveByKeyAsync<TRow>(
		ITable<TRow> table, IReadOnlyList<TRow> desired, CancellationToken ct)
		where TRow : TemporalRow
	{
		var keys = desired.Select(d => d.Key).Distinct().ToList();
		return await table
			.Where(x => x.ActiveTo == null && keys.Contains(x.Key))
			.ToDictionaryAsync(x => x.Key, ct);
	}

	// Plan-wide cursor: the max revision across the whole (per-scope) table.
	static async Task<long> MaxVersionAsync<TRow>(ITable<TRow> table, CancellationToken ct)
		where TRow : TemporalRow =>
		await table.Select(x => (long?)x.Version).MaxAsync(ct) ?? 0;

	// ── 2. classify each desired row against its active revision ─────────────

	sealed record Batch<TRow>(
		List<(string Key, long Version)> ToClose,
		List<TRow> ToInsert,
		List<TemporalConflict> Conflicts)
		where TRow : TemporalRow
	{
		public bool IsEmpty => ToClose.Count == 0 && ToInsert.Count == 0;
	}

	static Batch<TRow> Classify<TRow>(
		IReadOnlyList<TRow> desired, Dictionary<string, TRow> active, long nextVersion, DateTime now)
		where TRow : TemporalRow
	{
		var batch = new Batch<TRow>([], [], []);

		foreach (var d in desired)
		{
			active.TryGetValue(d.Key, out var current);

			if (current is null)
			{
				if (d.Version == 0)
					batch.ToInsert.Add(Revision(d, nextVersion, created: now, now));            // new node
				else
					batch.Conflicts.Add(new(d.Key, TemporalConflictKind.Vanished, d.Version, null));
			}
			else if (current.SamePayload(d))
			{
				// no-op: identical payload (a resubmit, or an identical concurrent edit)
			}
			else if (current.Version == d.Version)
			{
				batch.ToClose.Add((d.Key, d.Version));                                          // baseline current → legal edit
				batch.ToInsert.Add(Revision(d, nextVersion, created: current.Created, now));
			}
			else
			{
				batch.Conflicts.Add(new(d.Key, TemporalConflictKind.Stale, d.Version, current.Version)); // changed under the author
			}
		}

		return batch;
	}

	static TRow Revision<TRow>(TRow desired, long version, DateTime created, DateTime updated)
		where TRow : TemporalRow =>
		(TRow)desired.AsRevision(version, created, updated);

	// ── 3. apply atomically: close baselines, insert new revisions ───────────

	static async Task<TemporalUpsertResult> ApplyAsync<TRow>(
		DataConnection db, ITable<TRow> table, Batch<TRow> batch,
		long fromVersion, long nextVersion, DateTime now, CancellationToken ct)
		where TRow : TemporalRow
	{
		using var tx = await db.BeginTransactionAsync(ct);
		try
		{
			var closed = await CloseBaselinesAsync(table, batch.ToClose, activeTo: fromVersion, now, ct);
			if (closed != batch.ToClose.Count)
			{
				// the baseline row(s) we meant to close are no longer active:
				// a writer slipped into our read→write window. Abort, let caller retry.
				await tx.RollbackAsync(ct);
				return TemporalUpsertResult.Rejected(fromVersion,
					[new("*", TemporalConflictKind.CloseRace, fromVersion, null)]);
			}

			foreach (var row in batch.ToInsert)
				await db.InsertAsync(row, token: ct);

			await tx.CommitAsync(ct);
			return TemporalUpsertResult.Committed(fromVersion, nextVersion, batch.ToInsert.Count, closed);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}

	static async Task<int> CloseBaselinesAsync<TRow>(
		ITable<TRow> table, List<(string Key, long Version)> toClose,
		long activeTo, DateTime now, CancellationToken ct)
		where TRow : TemporalRow
	{
		if (toClose.Count == 0) return 0;

		var keys = toClose.Select(c => new { c.Key, c.Version }).ToList();
		return await table
			.Where(x => x.ActiveTo == null && new { x.Key, x.Version }.In(keys))
			.Set(x => x.ActiveTo, _ => (long?)activeTo)
			.Set(x => x.Updated, _ => now)
			.UpdateAsync(ct);
	}
}
