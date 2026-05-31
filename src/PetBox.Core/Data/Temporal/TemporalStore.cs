using LinqToDB;
using LinqToDB.Data;

namespace PetBox.Core.Data.Temporal;

public enum TemporalConflictKind
{
	// The active revision moved past the author's baseline before they submitted.
	Stale,

	// The author edited a node (baseline > 0) that no longer exists.
	Vanished,

	// A rename targets a Key that is already occupied by an active node.
	TargetOccupied,

	// A concurrent writer closed the baseline row inside our read→close window.
	CloseRace,
}

// One row the caller could not apply because the store moved under its baseline.
public sealed record TemporalConflict(
	string Key,
	TemporalConflictKind Kind,
	long BaselineVersion,
	long? ActiveVersion);

// Result of an upsert. Besides what was applied, carries the delta the caller
// asked for via `sinceVersion`: every active row that changed since that cursor
// (own edits AND others'), split into Added/Updated, plus keys that died. The
// caller advances its single cursor to CurrentVersion without re-reading.
public sealed record TemporalUpsertResult<TRow>(
	bool Applied,
	long CurrentVersion,
	int Inserted,
	int Closed,
	IReadOnlyList<TemporalConflict> Conflicts,
	IReadOnlyList<TRow> Added,
	IReadOnlyList<TRow> Updated,
	IReadOnlyList<string> Removed)
	where TRow : TemporalRow
{
	public bool HasConflicts => Conflicts.Count > 0;
}

// Declarative, append-only upsert for a batch of keyed rows.
//
// Each desired row carries the version its author last saw (Version == 0 = "new").
// Optimistic concurrency keys on the AUTHOR's baseline, not a freshly re-read
// version, so a change that landed during the author's think-time is caught. Any
// conflict aborts the whole batch; conflicts are returned so the caller rebases.
//
// A desired row may also carry PrevKey to express a rename/re-key: retire the
// active row at PrevKey and create a new identity at Key (linked via PrevKey).
//
// Meant to flow through a single-writer SQLite file (one DB per scope), which
// serialises writers and keeps the global Version a clean cursor.
//
// Flow: read current state → classify each row → apply → compute the delta.
public static class TemporalStore
{
	public static Task<TemporalUpsertResult<TRow>> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		long sinceVersion = 0,
		TimeProvider? time = null,
		CancellationToken ct = default)
		where TRow : TemporalRow =>
		UpsertAsync(db, desired, [], sinceVersion, time, onBeforeApply: null, ct);

	// Overload that also soft-deletes (closes the active row with no new revision) the
	// given keys — used by memory.upsert's `deleted:true`. version 0 = delete the
	// current active row regardless; a non-zero version that no longer matches yields a
	// Stale conflict; deleting a key with no active row is a no-op (idempotent).
	public static Task<TemporalUpsertResult<TRow>> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		IReadOnlyList<(string Key, long Version)> delete,
		long sinceVersion = 0,
		TimeProvider? time = null,
		CancellationToken ct = default)
		where TRow : TemporalRow =>
		UpsertAsync(db, desired, delete, sinceVersion, time, onBeforeApply: null, ct);

	// onBeforeApply is a test-only seam: it fires after classification but before
	// the close+insert transaction, to drive the CloseRace branch deterministically.
	internal static async Task<TemporalUpsertResult<TRow>> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		IReadOnlyList<(string Key, long Version)> delete,
		long sinceVersion,
		TimeProvider? time,
		Func<Task>? onBeforeApply,
		CancellationToken ct)
		where TRow : TemporalRow
	{
		var table = db.GetTable<TRow>();

		var active = await ActiveByKeyAsync(table, desired, delete, ct);
		var fromVersion = await MaxVersionAsync(table, ct);
		var nextVersion = fromVersion + 1;
		var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

		var batch = Classify(desired, active, nextVersion, now);

		// Soft-delete: close the active row, no replacement revision -> shows up in
		// the delta's `removed`.
		foreach (var (key, version) in delete)
		{
			active.TryGetValue(key, out var current);
			if (current is null)
				continue; // idempotent: already gone
			if (version != 0 && current.Version != version)
				batch.Conflicts.Add(new(key, TemporalConflictKind.Stale, version, current.Version));
			else
				batch.ToClose.Add((key, current.Version));
		}

		var conflicts = batch.Conflicts;
		var applied = conflicts.Count == 0;
		var inserted = 0;
		var closed = 0;

		if (applied && !batch.IsEmpty)
		{
			if (onBeforeApply is not null)
				await onBeforeApply();

			var race = await ApplyAsync(db, table, batch, nextVersion, now, ct);
			if (race is not null)
			{
				conflicts = [race];
				applied = false;
			}
			else
			{
				inserted = batch.ToInsert.Count;
				closed = batch.ToClose.Count;
			}
		}

		var (added, updated, removed) = await DeltaAsync(table, sinceVersion, ct);
		var currentVersion = await MaxVersionAsync(table, ct);

		return new TemporalUpsertResult<TRow>(applied, currentVersion, inserted, closed, conflicts, added, updated, removed);
	}

	// ── 1. read current state ────────────────────────────────────────────────

	static async Task<Dictionary<string, TRow>> ActiveByKeyAsync<TRow>(
		ITable<TRow> table, IReadOnlyList<TRow> desired, IReadOnlyList<(string Key, long Version)> delete, CancellationToken ct)
		where TRow : TemporalRow
	{
		// Renames need the active row at PrevKey too; deletes need their key's active row.
		var keys = desired.Select(d => d.Key)
			.Concat(desired.Where(d => d.PrevKey is not null).Select(d => d.PrevKey!))
			.Concat(delete.Select(t => t.Key))
			.Distinct()
			.ToList();
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
			if (d.PrevKey is not null)
			{
				// rename / re-key: retire PrevKey, create Key as a new linked identity
				active.TryGetValue(d.Key, out var occupied);
				active.TryGetValue(d.PrevKey, out var source);

				if (occupied is not null)
					batch.Conflicts.Add(new(d.Key, TemporalConflictKind.TargetOccupied, d.Version, occupied.Version));
				else if (source is null)
					batch.Conflicts.Add(new(d.PrevKey, TemporalConflictKind.Vanished, d.Version, null));
				else if (source.Version != d.Version)
					batch.Conflicts.Add(new(d.PrevKey, TemporalConflictKind.Stale, d.Version, source.Version));
				else
				{
					batch.ToClose.Add((d.PrevKey, d.Version));
					batch.ToInsert.Add(Revision(d, nextVersion, created: now, now)); // new identity -> Added in delta
				}
				continue;
			}

			active.TryGetValue(d.Key, out var current);

			if (current is null)
			{
				if (d.Version == 0)
					batch.ToInsert.Add(Revision(d, nextVersion, created: now, now));
				else
					batch.Conflicts.Add(new(d.Key, TemporalConflictKind.Vanished, d.Version, null));
			}
			else if (current.SamePayload(d))
			{
				// no-op: identical payload (a resubmit, or an identical concurrent edit)
			}
			else if (current.Version == d.Version)
			{
				batch.ToClose.Add((d.Key, d.Version));
				batch.ToInsert.Add(Revision(d, nextVersion, created: current.Created, now));
			}
			else
			{
				batch.Conflicts.Add(new(d.Key, TemporalConflictKind.Stale, d.Version, current.Version));
			}
		}

		return batch;
	}

	static TRow Revision<TRow>(TRow desired, long version, DateTime created, DateTime updated)
		where TRow : TemporalRow =>
		(TRow)desired.AsRevision(version, created, updated);

	// ── 3. apply atomically: close baselines, insert new revisions ───────────

	// Returns null on commit, or a CloseRace conflict if a baseline row was no
	// longer active (a writer slipped into our read→close window).
	static async Task<TemporalConflict?> ApplyAsync<TRow>(
		DataConnection db, ITable<TRow> table, Batch<TRow> batch, long nextVersion, DateTime now, CancellationToken ct)
		where TRow : TemporalRow
	{
		using var tx = await db.BeginTransactionAsync(ct);
		try
		{
			var closed = await CloseBaselinesAsync(table, batch.ToClose, activeTo: nextVersion, now, ct);
			if (closed != batch.ToClose.Count)
			{
				await tx.RollbackAsync(ct);
				return new("*", TemporalConflictKind.CloseRace, nextVersion - 1, null);
			}

			foreach (var row in batch.ToInsert)
				await db.InsertAsync(row, token: ct);

			await tx.CommitAsync(ct);
			return null;
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
			.Set(x => x.ActiveTo, _ => (long?)activeTo) // stamp the retiring version, so deaths are queryable by cursor
			.Set(x => x.Updated, _ => now)
			.UpdateAsync(ct);
	}

	// ── 4. delta since the caller's cursor ───────────────────────────────────

	static async Task<(List<TRow> Added, List<TRow> Updated, List<string> Removed)> DeltaAsync<TRow>(
		ITable<TRow> table, long sinceVersion, CancellationToken ct)
		where TRow : TemporalRow
	{
		var changed = await table.Where(x => x.ActiveTo == null && x.Version > sinceVersion).ToListAsync(ct);
		// Per-batch invariant: a row born this batch has Created == Updated; an
		// edited row carried its Created from a prior batch -> Created != Updated.
		var added = changed.Where(x => x.Created == x.Updated).ToList();
		var updated = changed.Where(x => x.Created != x.Updated).ToList();

		var died = await table
			.Where(x => x.ActiveTo != null && x.ActiveTo > sinceVersion)
			.Select(x => x.Key)
			.Distinct()
			.ToListAsync(ct);

		var removed = new List<string>();
		if (died.Count > 0)
		{
			var stillActive = await table
				.Where(x => x.ActiveTo == null && died.Contains(x.Key))
				.Select(x => x.Key)
				.ToListAsync(ct);
			removed = died.Except(stillActive).ToList();
		}

		return (added, updated, removed);
	}
}
