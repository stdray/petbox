using LinqToDB;
using LinqToDB.Data;

namespace PetBox.Core.Data.Temporal;

public enum TemporalConflictKind
{
	// The active revision moved past the author's baseline before they submitted.
	Stale,

	// The author edited a node (baseline > 0) that no longer exists.
	Vanished,

	// A concurrent writer closed the baseline row inside our own read→write window.
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
}

// Declarative, append-only upsert for a batch of keyed rows.
//
// Each desired row carries the version its author last saw (Version == 0 means
// "I believe this is new"). The engine classifies every row against the current
// active revision and applies the batch atomically:
//
//   active == null, baseline 0   -> insert new revision
//   active == null, baseline > 0 -> Vanished conflict (it was deleted)
//   payload unchanged            -> no-op (absorbs resubmits AND identical
//                                   concurrent edits)
//   active.Version == baseline   -> close baseline + insert new revision
//   active.Version != baseline   -> Stale conflict (changed under the author)
//
// Optimistic concurrency keys on the AUTHOR's baseline version, not on a freshly
// re-read version, so a change that landed any time during the author's
// think-time is caught. Any conflict aborts the whole batch (nothing is written)
// and the conflicts are returned so the caller can rebase and resubmit.
//
// The batch is meant to flow through a single-writer SQLite file (one DB per
// scope), which serialises writers and keeps the global Version a clean cursor.
public static class TemporalStore
{
	public static async Task<TemporalUpsertResult> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		CancellationToken ct = default)
		where TRow : TemporalRow
	{
		var table = db.GetTable<TRow>();
		var keys = desired.Select(d => d.Key).Distinct().ToList();

		var active = await table
			.Where(x => x.ActiveTo == null && keys.Contains(x.Key))
			.ToDictionaryAsync(x => x.Key, ct);

		// Global plan version = max revision across the whole (per-scope) table.
		var fromVersion = await table.Select(x => (long?)x.Version).MaxAsync(ct) ?? 0;
		var nextVersion = fromVersion + 1;
		var now = DateTime.UtcNow;

		var conflicts = new List<TemporalConflict>();
		var toClose = new List<(string Key, long Version)>();
		var toInsert = new List<TRow>();

		foreach (var d in desired)
		{
			active.TryGetValue(d.Key, out var cur);
			if (cur is null)
			{
				if (d.Version != 0)
					conflicts.Add(new(d.Key, TemporalConflictKind.Vanished, d.Version, null));
				else
					toInsert.Add((TRow)d.AsRevision(nextVersion, now, now));
			}
			else if (cur.SamePayload(d))
			{
				// no-op: identical payload, regardless of baseline version
			}
			else if (cur.Version == d.Version)
			{
				toClose.Add((d.Key, d.Version));
				toInsert.Add((TRow)d.AsRevision(nextVersion, cur.Created, now));
			}
			else
			{
				conflicts.Add(new(d.Key, TemporalConflictKind.Stale, d.Version, cur.Version));
			}
		}

		if (conflicts.Count > 0)
			return new(false, fromVersion, fromVersion, 0, 0, conflicts);

		if (toClose.Count == 0 && toInsert.Count == 0)
			return new(true, fromVersion, fromVersion, 0, 0, conflicts);

		using var tx = await db.BeginTransactionAsync(ct);
		try
		{
			var closed = 0;
			if (toClose.Count > 0)
			{
				var closeKeys = toClose.Select(c => new { c.Key, c.Version }).ToList();
				closed = await table
					.Where(x => x.ActiveTo == null && new { x.Key, x.Version }.In(closeKeys))
					.Set(x => x.ActiveTo, _ => (long?)fromVersion)
					.Set(x => x.Updated, _ => now)
					.UpdateAsync(ct);

				// The baseline row(s) we meant to close are no longer active: a
				// writer slipped into our read→write window. Abort, let caller retry.
				if (closed != toClose.Count)
				{
					await tx.RollbackAsync(ct);
					return new(false, fromVersion, fromVersion, 0, 0,
						[new("*", TemporalConflictKind.CloseRace, fromVersion, null)]);
				}
			}

			foreach (var row in toInsert)
				await db.InsertAsync(row, token: ct);

			await tx.CommitAsync(ct);
			return new(true, fromVersion, nextVersion, toInsert.Count, closed, conflicts);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}
}
