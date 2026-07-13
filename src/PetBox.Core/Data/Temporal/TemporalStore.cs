using System.Linq.Expressions;
using LinqToDB;
using LinqToDB.Data;

namespace PetBox.Core.Data.Temporal;

public enum TemporalConflictKind
{
	// The active revision moved PAST the author's baseline before they submitted — a
	// concurrent edit landed in think-time. Baseline is a watermark: this fires only when
	// the entity's current version is strictly newer than the baseline the author read.
	Stale,

	// A rename whose source (PrevKey) has no active row — the identity to retire is gone.
	// (A plain edit of a vanished key is no longer Vanished: with a baseline ≤ the scope
	// cursor there is nothing to clobber, so it re-creates; a baseline above the cursor is
	// FutureBaseline.)
	Vanished,

	// A rename targets a Key that is already occupied by an active node.
	TargetOccupied,

	// A concurrent writer closed the baseline row inside our read→close window.
	CloseRace,

	// The baseline quotes a version this scope never reached (> the scope cursor) — almost
	// always a currentVersion carried over from a DIFFERENT board/store. Reason spells out
	// the two version spaces. Caught before any other classification.
	FutureBaseline,

	// A domain guard refused the row (see Reason). TemporalStore itself never produces
	// this — services report guard rejections in the conflict shape so one batch outcome
	// (applied:false + conflicts) covers both concurrency and domain refusals.
	Rejected,
}

// One row the caller could not apply because the store moved under its baseline
// (or a domain guard refused it — then Reason says why). On a Stale conflict,
// ChangedFields names the payload fields of THIS entity that differ between the
// revision the author read and the current one (entity-scoped by construction —
// never other entities' noise), so a retry is informed, not blind.
public sealed record TemporalConflict(
	string Key,
	TemporalConflictKind Kind,
	long BaselineVersion,
	long? ActiveVersion,
	string? Reason = null,
	IReadOnlyList<string>? ChangedFields = null);

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
	IReadOnlyList<string> Removed,
	// Keys whose baseline was stale but whose entity payload had NOT semantically moved
	// since the author's read (bookkeeping rewrites only, incl. A→B→A) — applied instead
	// of conflicting, and reported here so the resolution stays visible, never silent.
	IReadOnlyList<string> AutoResolved)
	where TRow : TemporalRow
{
	public bool HasConflicts => Conflicts.Count > 0;
}

// How a batch decides between all-or-nothing and per-entry (partial) application.
//
// ATOMIC (the default, and the only behavior a caller gets without asking): any conflict
// aborts the WHOLE batch — nothing is written. Bit-for-bit the historical contract.
//
// PARTIAL (`Atomic: false`, an explicit opt-in): a conflict rejects THAT ENTRY only; the
// rest of the batch lands. The rejection SCOPE changes — the conflict POLICY does not: a
// Stale row is still exactly what the watermark says is stale (and an auto-resolved row is
// still applied, never rejected). Three inputs shape it:
//   * Rejected — domain-guard refusals the SERVICE already decided (kind Rejected, one per
//     key, with the reason). They ride the same shape as concurrency conflicts, so a caller
//     reads one `conflicts[]` for both;
//   * DependsOn — INTRA-BATCH references: key -> the keys of THIS batch it points at. A
//     rejected key cascades to everything that depends on it, directly or transitively, so a
//     partial apply never leaves a dangling reference. Cycle-safe (fixpoint over a growing
//     rejected set: a cycle with no rejected member simply never fires).
//     Null/empty = no intra-batch references, so the cascade degenerates to "every entry is
//     independent" — the correct, not the deficient, behavior for a store whose rows cannot
//     reference each other (config bindings, memory entries).
public sealed record TemporalBatchPolicy(
	bool Atomic = true,
	IReadOnlyList<TemporalConflict>? Rejected = null,
	IReadOnlyDictionary<string, IReadOnlyList<string>>? DependsOn = null)
{
	public static readonly TemporalBatchPolicy AllOrNothing = new();
}

// Declarative, append-only upsert for a batch of keyed rows.
//
// Each desired row carries a WATERMARK baseline in Version: the version the author
// last saw for THIS entity, OR the scope cursor (currentVersion) from their last read
// of the whole scope — either is a valid baseline (Version == 0 = "new, read nothing").
// Optimistic concurrency is a watermark, not an exact match — and it guards PAYLOAD,
// not version arithmetic:
//   * an EDIT applies when its baseline is AT OR AFTER the entity's current revision
//     (the entity was unchanged since the author's read);
//   * a payload IDENTICAL to the current revision is a no-op on any non-future baseline —
//     the store already holds what the author wants (a resubmit, an identical concurrent
//     edit, or an FSM effect that got there first), so there is nothing to protect;
//   * a baseline BEHIND the entity's revision is Stale only when the payload moved
//     SEMANTICALLY after the author's read; if the intervening revisions left the payload
//     as the author read it (bookkeeping rewrites, A→B→A), the edit applies and the key is
//     reported in AutoResolved. A genuine Stale carries ChangedFields — the entity's own
//     fields that moved — so the retry is informed, not blind;
//   * a CREATE (no active row) applies for ANY baseline ≤ the scope cursor, 0 included —
//     there is nothing to clobber;
//   * a baseline ABOVE the scope cursor is a FutureBaseline conflict — the author is
//     quoting a version this scope never reached, usually a cursor from another
//     board/store. Checked before everything, including the identical-payload no-op.
// So a caller may stamp every row in a batch with the single scope currentVersion it
// last read: the touched entity applies, untouched entities collapse to no-ops, and only
// a genuine concurrent change to a specific entity rejects. Any conflict aborts the whole
// batch; conflicts are returned so the caller rebases.
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
	// onWithinTx (when set) fires INSIDE the apply transaction, after the new revisions are
	// inserted and before commit, with the rows upserted this batch and the keys whose active
	// row was closed without a replacement (soft-deletes + rename sources). It is the seam the
	// Class-A search floor rides: writing the lexical index here commits/rolls back WITH the
	// entity, so a committed entity is never lexically-stale and a throw rolls both back together.
	public static Task<TemporalUpsertResult<TRow>> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		long sinceVersion = 0,
		Func<DataConnection, IReadOnlyList<TRow>, IReadOnlyList<string>, CancellationToken, Task>? onWithinTx = null,
		TimeProvider? time = null,
		// When set, scopes every read/close/delta to a partition within the table (e.g.
		// one board's rows in a shared plan_nodes), so several scopes can share one file
		// with independent keys + per-partition version cursors. Null = whole table.
		Expression<Func<TRow, bool>>? partition = null,
		CancellationToken ct = default)
		where TRow : TemporalRow =>
		UpsertAsync(db, desired, [], TemporalBatchPolicy.AllOrNothing, sinceVersion, time, onBeforeApply: null, onWithinTx, partition, ct);

	// Overload that also soft-deletes (closes the active row with no new revision) the
	// given keys — used by memory_upsert's `deleted:true`. version 0 = delete the
	// current active row regardless; a non-zero version that no longer matches yields a
	// Stale conflict; deleting a key with no active row is a no-op (idempotent).
	public static Task<TemporalUpsertResult<TRow>> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		IReadOnlyList<(string Key, long Version)> delete,
		long sinceVersion = 0,
		Func<DataConnection, IReadOnlyList<TRow>, IReadOnlyList<string>, CancellationToken, Task>? onWithinTx = null,
		TimeProvider? time = null,
		Expression<Func<TRow, bool>>? partition = null,
		CancellationToken ct = default)
		where TRow : TemporalRow =>
		UpsertAsync(db, desired, delete, TemporalBatchPolicy.AllOrNothing, sinceVersion, time, onBeforeApply: null, onWithinTx, partition, ct);

	// The policy overload: the SAME engine, told whether the batch is all-or-nothing (the
	// default) or partial-apply, plus the service's own guard rejections and the intra-batch
	// reference graph the cascade rides. Every batch-upsert verb goes through this one seam —
	// the topological cascade and the conflicts[] shape are never re-implemented per verb.
	public static Task<TemporalUpsertResult<TRow>> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		IReadOnlyList<(string Key, long Version)> delete,
		TemporalBatchPolicy policy,
		long sinceVersion = 0,
		Func<DataConnection, IReadOnlyList<TRow>, IReadOnlyList<string>, CancellationToken, Task>? onWithinTx = null,
		TimeProvider? time = null,
		Expression<Func<TRow, bool>>? partition = null,
		CancellationToken ct = default)
		where TRow : TemporalRow =>
		UpsertAsync(db, desired, delete, policy, sinceVersion, time, onBeforeApply: null, onWithinTx, partition, ct);

	// onBeforeApply is a test-only seam: it fires after classification but before
	// the close+insert transaction, to drive the CloseRace branch deterministically.
	internal static async Task<TemporalUpsertResult<TRow>> UpsertAsync<TRow>(
		DataConnection db,
		IReadOnlyList<TRow> desired,
		IReadOnlyList<(string Key, long Version)> delete,
		TemporalBatchPolicy policy,
		long sinceVersion,
		TimeProvider? time,
		Func<Task>? onBeforeApply,
		Func<DataConnection, IReadOnlyList<TRow>, IReadOnlyList<string>, CancellationToken, Task>? onWithinTx,
		Expression<Func<TRow, bool>>? partition,
		CancellationToken ct)
		where TRow : TemporalRow
	{
		var table = db.GetTable<TRow>();

		var active = await ActiveByKeyAsync(table, desired, delete, partition, ct);
		var fromVersion = await MaxVersionAsync(table, partition, ct);
		var nextVersion = fromVersion + 1;
		var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

		// The revision each stale-suspect author actually read (per key: the latest revision
		// with Version <= their baseline) — the reference point for telling a SEMANTIC move
		// (payload changed after the read → genuine conflict, with the changed fields named)
		// from a bookkeeping rewrite (payload identical → the write auto-resolves).
		var baselines = await BaselineRevisionsAsync(table, desired, delete, active, partition, ct);

		// One classification pass over a (sub)set of the call — pure, in-memory, and re-runnable:
		// partial-apply re-runs it on the survivors so ToInsert/ToClose can never disagree with
		// the set of entries that were actually kept.
		Batch<TRow> BuildBatch(IReadOnlyList<TRow> rows, IReadOnlyList<(string Key, long Version)> dels)
		{
			var b = Classify(rows, active, baselines, fromVersion, nextVersion, now);

			// Soft-delete: close the active row, no replacement revision -> shows up in
			// the delta's `removed`. Same watermark rules as an edit: a baseline above the
			// scope cursor is FutureBaseline; a baseline that predates the entity's current
			// revision is Stale unless the payload never semantically moved since the read;
			// anything ≥ the current revision (0 = delete regardless) closes.
			foreach (var (key, version) in dels)
			{
				active.TryGetValue(key, out var current);
				if (current is null)
					continue; // idempotent: already gone
				if (version > fromVersion)
					b.Conflicts.Add(new(key, TemporalConflictKind.FutureBaseline, version, fromVersion, FutureBaselineReason(version, fromVersion)));
				else if (version != 0 && version < current.Version)
				{
					var read = baselines.GetValueOrDefault(key);
					if (read is not null && read.SamePayload(current))
					{
						b.ToClose.Add((key, current.Version));
						b.AutoResolved.Add(key);
					}
					else
						b.Conflicts.Add(StaleConflict(key, version, current, read));
				}
				else
					b.ToClose.Add((key, current.Version));
			}
			return b;
		}

		var batch = BuildBatch(desired, delete);

		// The call's conflicts = what classification refused (Stale/FutureBaseline/…) PLUS the
		// domain-guard refusals the service already decided. Both shapes, one list.
		var conflicts = new List<TemporalConflict>(batch.Conflicts);
		if (policy.Rejected is { Count: > 0 })
			conflicts.AddRange(policy.Rejected);

		bool applied;
		if (policy.Atomic)
		{
			// All-or-nothing: any conflict aborts the whole batch — unchanged, bit for bit.
			applied = conflicts.Count == 0;
		}
		else if (conflicts.Count == 0)
		{
			applied = true; // nothing to reject — partial mode collapses to the ordinary write
		}
		else
		{
			// Partial: the rejected entries (and, transitively, everything in THIS batch that
			// references them) drop out; the batch is re-classified over the survivors and the
			// cascade reasons join conflicts[]. An entry auto-resolved by the watermark is NOT
			// rejected, so it neither drops out nor cascades.
			var rejected = RejectedKeys(desired, delete, conflicts);
			conflicts.AddRange(CascadeRejections(desired, delete, rejected, policy.DependsOn));

			var survivors = desired.Where(d => !IsRejected(d, rejected)).ToList();
			var survivingDeletes = delete.Where(t => !rejected.Contains(t.Key)).ToList();
			batch = BuildBatch(survivors, survivingDeletes);
			batch.Conflicts.Clear(); // survivors classify clean by construction — their refusals are already in `conflicts`
			applied = !batch.IsEmpty; // nothing survived => nothing landed
		}

		var inserted = 0;
		var closed = 0;

		if (applied && !batch.IsEmpty)
		{
			if (onBeforeApply is not null)
				await onBeforeApply();

			var race = await ApplyAsync(db, table, batch, nextVersion, now, onWithinTx, partition, ct);
			if (race is not null)
			{
				// A lost close race kills the write that was actually attempted — in partial mode
				// the per-entry refusals stay in the list, with the race appended to them.
				conflicts = policy.Atomic ? [race] : [.. conflicts, race];
				applied = false;
			}
			else
			{
				inserted = batch.ToInsert.Count;
				closed = batch.ToClose.Count;
			}
		}

		var (added, updated, removed) = await DeltaAsync(table, sinceVersion, partition, ct);
		var currentVersion = await MaxVersionAsync(table, partition, ct);

		// AutoResolved is only meaningful when the batch landed (a conflict elsewhere aborts
		// the whole batch, so a "resolved" row was not actually written). In partial mode
		// `batch` is already the surviving subset, so this names exactly the rows that landed.
		var autoResolved = applied ? (IReadOnlyList<string>)batch.AutoResolved : [];
		return new TemporalUpsertResult<TRow>(applied, currentVersion, inserted, closed, conflicts, added, updated, removed, autoResolved);
	}

	// Standalone delta read: the active rows that changed since `sinceVersion` (split into
	// Added/Updated like an upsert result), the keys that died, and the current max version to
	// advance a cursor to. The async-vectorization worker's source rides this to subscribe to a
	// store's temporal log without performing a write — no separate outbox table needed.
	public static async Task<(IReadOnlyList<TRow> Added, IReadOnlyList<TRow> Updated, IReadOnlyList<string> Removed, long CurrentVersion)> ChangesSinceAsync<TRow>(
		DataConnection db, long sinceVersion, Expression<Func<TRow, bool>>? partition = null, CancellationToken ct = default)
		where TRow : TemporalRow
	{
		var table = db.GetTable<TRow>();
		var (added, updated, removed) = await DeltaAsync(table, sinceVersion, partition, ct);
		var current = await MaxVersionAsync(table, partition, ct);
		return (added, updated, removed, current);
	}

	// ── 1b. partial-apply: who is rejected, and who depends on them ──────────

	// The batch keys a conflict names. A conflict on a RENAME quotes the source (PrevKey) —
	// the entry that carries it is keyed by its new Key, so both spellings map back to it.
	static HashSet<string> RejectedKeys<TRow>(
		IReadOnlyList<TRow> desired, IReadOnlyList<(string Key, long Version)> delete, List<TemporalConflict> conflicts)
		where TRow : TemporalRow
	{
		var named = conflicts.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
		var rejected = new HashSet<string>(StringComparer.Ordinal);
		foreach (var d in desired)
			if (named.Contains(d.Key) || (d.PrevKey is not null && named.Contains(d.PrevKey)))
				rejected.Add(d.Key);
		foreach (var (key, _) in delete)
			if (named.Contains(key)) rejected.Add(key);
		return rejected;
	}

	static bool IsRejected<TRow>(TRow d, HashSet<string> rejected) where TRow : TemporalRow =>
		rejected.Contains(d.Key) || (d.PrevKey is not null && rejected.Contains(d.PrevKey));

	static List<TemporalConflict> CascadeRejections<TRow>(
		IReadOnlyList<TRow> desired, IReadOnlyList<(string Key, long Version)> delete,
		HashSet<string> rejected, IReadOnlyDictionary<string, IReadOnlyList<string>>? dependsOn)
		where TRow : TemporalRow
	{
		var baseline = new Dictionary<string, long>(StringComparer.Ordinal);
		foreach (var d in desired) baseline[d.Key] = d.Version;
		foreach (var (key, version) in delete) baseline.TryAdd(key, version);
		var order = desired.Select(d => d.Key).Concat(delete.Select(t => t.Key))
			.Distinct(StringComparer.Ordinal).ToList();
		return Cascade(order, k => baseline.GetValueOrDefault(k), rejected, dependsOn);
	}

	// THE cascade — the one implementation, shared by the engine and by any service that must
	// reject entries BEFORE the batch reaches the store (a domain guard that runs on resolved
	// references cannot re-run meaningfully once a referenced entry is gone, so it drops the
	// dependents up front, with THIS reason rather than a misleading "unresolved ref").
	//
	// An entry that points (directly or through a chain) at a rejected entry of the SAME call is
	// itself rejected: applying it would leave a dangling reference. Fixpoint over a
	// monotonically growing rejected set, so a reference CYCLE cannot spin — a cycle whose
	// members are all clean never fires; a cycle that touches a rejected key collapses in one
	// sweep. `rejected` is MUTATED to the full transitive closure.
	public static List<TemporalConflict> Cascade(
		IReadOnlyList<string> order,
		Func<string, long> baselineOf,
		HashSet<string> rejected,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? dependsOn)
	{
		var cascade = new List<TemporalConflict>();
		if (dependsOn is not { Count: > 0 }) return cascade; // no intra-batch refs — every entry is independent

		for (var moved = true; moved;)
		{
			moved = false;
			foreach (var key in order)
			{
				if (rejected.Contains(key)) continue;
				if (!dependsOn.TryGetValue(key, out var deps)) continue;
				var on = deps.FirstOrDefault(rejected.Contains);
				if (on is null) continue;
				rejected.Add(key);
				moved = true;
				cascade.Add(new(key, TemporalConflictKind.Rejected, baselineOf(key), null,
					$"depends on '{on}', which this batch rejected — applying it would leave a dangling reference"));
			}
		}
		return cascade;
	}

	// ── 1. read current state ────────────────────────────────────────────────

	static async Task<Dictionary<string, TRow>> ActiveByKeyAsync<TRow>(
		ITable<TRow> table, IReadOnlyList<TRow> desired, IReadOnlyList<(string Key, long Version)> delete,
		Expression<Func<TRow, bool>>? partition, CancellationToken ct)
		where TRow : TemporalRow
	{
		// Renames need the active row at PrevKey too; deletes need their key's active row.
		var keys = desired.Select(d => d.Key)
			.Concat(desired.Where(d => d.PrevKey is not null).Select(d => d.PrevKey!))
			.Concat(delete.Select(t => t.Key))
			.Distinct()
			.ToList();
		var q = table.Where(x => x.ActiveTo == null && keys.Contains(x.Key));
		if (partition is not null) q = q.Where(partition);
		return await q.ToDictionaryAsync(x => x.Key, ct);
	}

	// Cursor: the max revision within the partition (or the whole table when unpartitioned).
	static async Task<long> MaxVersionAsync<TRow>(ITable<TRow> table, Expression<Func<TRow, bool>>? partition, CancellationToken ct)
		where TRow : TemporalRow
	{
		IQueryable<TRow> q = table;
		if (partition is not null) q = q.Where(partition);
		return await q.Select(x => (long?)x.Version).MaxAsync(ct) ?? 0;
	}

	// For every row that will hit the stale branch (baseline > 0 and behind the entity's
	// current revision, payload not already identical), fetch the revision the author
	// actually read: the entity's latest revision with Version <= the baseline (history
	// rows are stamped with scope-cursor values, so a scope-cursor baseline resolves the
	// same way as an entity-version baseline). One point query per suspect key — the
	// conflict path is rare, so this costs nothing on the happy path.
	static async Task<Dictionary<string, TRow>> BaselineRevisionsAsync<TRow>(
		ITable<TRow> table, IReadOnlyList<TRow> desired, IReadOnlyList<(string Key, long Version)> delete,
		Dictionary<string, TRow> active, Expression<Func<TRow, bool>>? partition, CancellationToken ct)
		where TRow : TemporalRow
	{
		var wanted = new Dictionary<string, long>();
		foreach (var d in desired)
			if (d.PrevKey is null && d.Version > 0
				&& active.TryGetValue(d.Key, out var cur) && d.Version < cur.Version && !cur.SamePayload(d))
				wanted.TryAdd(d.Key, d.Version);
		foreach (var (key, version) in delete)
			if (version > 0 && active.TryGetValue(key, out var cur) && version < cur.Version)
				wanted.TryAdd(key, version);

		var result = new Dictionary<string, TRow>();
		foreach (var (key, baseline) in wanted)
		{
			var q = table.Where(x => x.Key == key && x.Version <= baseline);
			if (partition is not null) q = q.Where(partition);
			var read = await q.OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
			if (read is not null) result[key] = read;
		}
		return result;
	}

	// ── 2. classify each desired row against its active revision ─────────────

	sealed record Batch<TRow>(
		List<(string Key, long Version)> ToClose,
		List<TRow> ToInsert,
		List<TemporalConflict> Conflicts,
		List<string> AutoResolved)
		where TRow : TemporalRow
	{
		public bool IsEmpty => ToClose.Count == 0 && ToInsert.Count == 0;
	}

	static Batch<TRow> Classify<TRow>(
		IReadOnlyList<TRow> desired, Dictionary<string, TRow> active, Dictionary<string, TRow> baselines,
		long fromVersion, long nextVersion, DateTime now)
		where TRow : TemporalRow
	{
		var batch = new Batch<TRow>([], [], [], []);

		foreach (var d in desired)
		{
			// A baseline above the scope cursor is a wrong-scope quote — reject before any
			// other classification (and before SamePayload: an identical payload with a
			// future baseline is a conflict, not a silent no-op).
			if (d.Version > fromVersion)
			{
				batch.Conflicts.Add(new(d.Key, TemporalConflictKind.FutureBaseline, d.Version, fromVersion, FutureBaselineReason(d.Version, fromVersion)));
				continue;
			}

			if (d.PrevKey is not null)
			{
				// rename / re-key: retire PrevKey, create Key as a new linked identity
				active.TryGetValue(d.Key, out var occupied);
				active.TryGetValue(d.PrevKey, out var source);

				if (occupied is not null)
					batch.Conflicts.Add(new(d.Key, TemporalConflictKind.TargetOccupied, d.Version, occupied.Version));
				else if (source is null)
					batch.Conflicts.Add(new(d.PrevKey, TemporalConflictKind.Vanished, d.Version, null));
				else if (d.Version < source.Version) // source moved past the author's baseline
					batch.Conflicts.Add(new(d.PrevKey, TemporalConflictKind.Stale, d.Version, source.Version));
				else
				{
					batch.ToClose.Add((d.PrevKey, source.Version)); // close the real active revision, not the (possibly higher) baseline
					batch.ToInsert.Add(Revision(d, nextVersion, created: now, now)); // new identity -> Added in delta
				}
				continue;
			}

			active.TryGetValue(d.Key, out var current);

			if (current is null)
			{
				// No active row + baseline ≤ the scope cursor: a create. Nothing to clobber, so
				// ANY such baseline (0 or a real cursor) is valid. NOTE: a key whose row was
				// closed AFTER the author's read re-creates the identity rather than raising a
				// "deleted after baseline" conflict — detecting it would need an extra closed-row
				// query the classifier deliberately avoids.
				batch.ToInsert.Add(Revision(d, nextVersion, created: now, now));
			}
			else if (current.SamePayload(d))
			{
				// no-op: the store already holds exactly what the author wants — a resubmit, an
				// identical concurrent edit, or an FSM effect that already made this change.
				// Deliberately BEFORE the stale check (but after FutureBaseline, which still
				// teaches a wrong-scope quote): forcing a re-read round-trip when there is
				// nothing to rebase is the blind-retry defect, not protection (intake
				// stale-baseline-blind-retry).
			}
			else if (d.Version < current.Version) // entity moved past the author's baseline
			{
				// Only a SEMANTIC move rejects (spec baseline-watermark: rejected is exactly
				// what changed after the author's read). If every intervening revision left
				// the payload as the author read it (bookkeeping rewrites, A→B→A), the read
				// is still fresh in substance — apply, and report the key in AutoResolved.
				var read = baselines.GetValueOrDefault(d.Key);
				if (read is not null && read.SamePayload(current))
				{
					batch.ToClose.Add((d.Key, current.Version));
					batch.ToInsert.Add(Revision(d, nextVersion, created: current.Created, now));
					batch.AutoResolved.Add(d.Key);
				}
				else
					batch.Conflicts.Add(StaleConflict(d.Key, d.Version, current, read));
			}
			else
			{
				batch.ToClose.Add((d.Key, current.Version)); // close the real active revision, not the (possibly higher) baseline
				batch.ToInsert.Add(Revision(d, nextVersion, created: current.Created, now));
			}
		}

		return batch;
	}

	// The FutureBaseline message: teach the two version spaces and the fix.
	static string FutureBaselineReason(long baseline, long cursor) =>
		$"your baseline {baseline} is ahead of this scope's cursor {cursor} — a version from another board/scope? " +
		"pass the currentVersion from your last read of THIS scope (or the entity's own version); 0 = new entity.";

	// An INFORMED Stale: names the payload fields of this entity that moved past the
	// author's baseline (from ChangedPayloadFields — entity-scoped by construction).
	// `read` is null when the entity did not exist at the baseline (created after the
	// author's read, or baseline 0 on an existing key) — then there is nothing to diff.
	static TemporalConflict StaleConflict<TRow>(string key, long baseline, TRow current, TRow? read)
		where TRow : TemporalRow
	{
		var fields = read is null ? null : current.ChangedPayloadFields(read);
		var reason =
			fields is { Count: > 0 }
				? $"changed after your baseline {baseline}: {string.Join(", ", fields)} — re-read and rebase on version {current.Version}"
			: read is null
				? baseline == 0
					? $"an active row already exists at version {current.Version} — you submitted baseline 0 (new); re-read before overwriting"
					: $"the entity was created after your baseline {baseline} — you never read it; re-read before overwriting"
			: null; // payload differs but this row type names no fields — keep the classic terse Stale
		return new(key, TemporalConflictKind.Stale, baseline, current.Version, reason,
			fields is { Count: > 0 } ? fields : null);
	}

	static TRow Revision<TRow>(TRow desired, long version, DateTime created, DateTime updated)
		where TRow : TemporalRow =>
		(TRow)desired.AsRevision(version, created, updated);

	// ── 3. apply atomically: close baselines, insert new revisions ───────────

	// Returns null on commit, or a CloseRace conflict if a baseline row was no
	// longer active (a writer slipped into our read→close window).
	static async Task<TemporalConflict?> ApplyAsync<TRow>(
		DataConnection db, ITable<TRow> table, Batch<TRow> batch, long nextVersion, DateTime now,
		Func<DataConnection, IReadOnlyList<TRow>, IReadOnlyList<string>, CancellationToken, Task>? onWithinTx,
		Expression<Func<TRow, bool>>? partition, CancellationToken ct)
		where TRow : TemporalRow
	{
		using var tx = await db.BeginTransactionAsync(ct);
		try
		{
			var closed = await CloseBaselinesAsync(table, batch.ToClose, activeTo: nextVersion, now, partition, ct);
			if (closed != batch.ToClose.Count)
			{
				await tx.RollbackAsync(ct);
				return new("*", TemporalConflictKind.CloseRace, nextVersion - 1, null);
			}

			foreach (var row in batch.ToInsert)
				await db.InsertAsync(row, token: ct);

			if (onWithinTx is not null)
			{
				// Keys closed without a replacement revision = soft-deletes + rename sources;
				// an edit closes AND re-inserts the same key, so it stays an upsert, not a delete.
				var deletedKeys = batch.ToClose.Select(c => c.Key)
					.Except(batch.ToInsert.Select(r => r.Key))
					.ToList();
				await onWithinTx(db, batch.ToInsert, deletedKeys, ct);
			}

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
		long activeTo, DateTime now, Expression<Func<TRow, bool>>? partition, CancellationToken ct)
		where TRow : TemporalRow
	{
		if (toClose.Count == 0) return 0;

		var keys = toClose.Select(c => new { c.Key, c.Version }).ToList();
		// Partition filter is essential here: (Key, Version) can collide across partitions
		// sharing the table, so an unscoped close could retire another partition's row.
		var q = table.Where(x => x.ActiveTo == null && new { x.Key, x.Version }.In(keys));
		if (partition is not null) q = q.Where(partition);
		return await q
			.Set(x => x.ActiveTo, _ => (long?)activeTo) // stamp the retiring version, so deaths are queryable by cursor
			.Set(x => x.Updated, _ => now)
			.UpdateAsync(ct);
	}

	// ── 4. delta since the caller's cursor ───────────────────────────────────

	static async Task<(List<TRow> Added, List<TRow> Updated, List<string> Removed)> DeltaAsync<TRow>(
		ITable<TRow> table, long sinceVersion, Expression<Func<TRow, bool>>? partition, CancellationToken ct)
		where TRow : TemporalRow
	{
		var changedQ = table.Where(x => x.ActiveTo == null && x.Version > sinceVersion);
		if (partition is not null) changedQ = changedQ.Where(partition);
		var changed = await changedQ.ToListAsync(ct);
		// Per-batch invariant: a row born this batch has Created == Updated; an
		// edited row carried its Created from a prior batch -> Created != Updated.
		var added = changed.Where(x => x.Created == x.Updated).ToList();
		var updated = changed.Where(x => x.Created != x.Updated).ToList();

		var diedQ = table.Where(x => x.ActiveTo != null && x.ActiveTo > sinceVersion);
		if (partition is not null) diedQ = diedQ.Where(partition);
		var died = await diedQ.Select(x => x.Key).Distinct().ToListAsync(ct);

		var removed = new List<string>();
		if (died.Count > 0)
		{
			var stillQ = table.Where(x => x.ActiveTo == null && died.Contains(x.Key));
			if (partition is not null) stillQ = stillQ.Where(partition);
			var stillActive = await stillQ.Select(x => x.Key).ToListAsync(ct);
			removed = died.Except(stillActive).ToList();
		}

		return (added, updated, removed);
	}
}
