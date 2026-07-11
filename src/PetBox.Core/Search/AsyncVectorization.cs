using Microsoft.Extensions.Logging;

namespace PetBox.Core.Search;

// Async materialization of Class-B (enriching) indexes — the write path NEVER blocks on
// embedding (spec: write-never-blocks). A Class-B index is just a delta SUBSCRIBER with its own
// cursor over the entity's existing temporal version log: no separate outbox table, per-index
// state is one number. An embedder outage stalls the cursor; on recovery it drains forward =
// automatic backfill with no lost writes (spec: durable-backfill). A poison item (always fails)
// is dead-lettered after N attempts so it can't head-of-line-block the cursor. Design: m-1a5c37fe.

// An entity (scope, type, id) — the address of a doc to drop from a Class-B index.
public readonly record struct DocRef(string Scope, string Type, string Id);

// A batch of changes since a cursor, plus the cursor to advance to once the batch is fully
// materialized. (Maps onto TemporalStore.DeltaAsync: Upserts = Added∪Updated, Deletes = Removed,
// CurrentVersion = the store's max version.)
public sealed record SourceDelta(
	IReadOnlyList<SearchDoc> Upserts,
	IReadOnlyList<DocRef> Deletes,
	long CurrentVersion);

// Yields the change-delta of one entity store since a version cursor. The adapter over a concrete
// store (memory entries, plan nodes, session log) is the consumer's; this keeps the worker generic.
public interface ISearchSource
{
	Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default);
}

// Durable per-index state: the version cursor, a per-entity attempt counter, and a dead-letter
// set. In-memory impl ships for tests/dev (below); a SQLite-backed impl rides the store file in
// the memory/tasks retrofit.
public interface IIndexCursorStore
{
	Task<long> GetCursorAsync(string index, CancellationToken ct = default);
	Task SetCursorAsync(string index, long version, CancellationToken ct = default);
	Task<int> BumpAttemptsAsync(string index, string type, string id, CancellationToken ct = default);
	Task ClearAttemptsAsync(string index, string type, string id, CancellationToken ct = default);
	Task MarkDeadAsync(string index, string type, string id, CancellationToken ct = default);
	Task<bool> IsDeadAsync(string index, string type, string id, CancellationToken ct = default);
}

// The outcome of one drain pass. `SourceVersion` is the source's max version at the start of the
// pass: SourceVersion − Cursor is the index's LAG, the number that says "semantic search here is
// behind by N versions" (0 = caught up). Carried out so a job can log/alert on it.
public sealed record DrainResult(int Indexed, int Deleted, int DeadLettered, bool Advanced, long Cursor, long SourceVersion = 0)
{
	public long Lag => Math.Max(0, SourceVersion - Cursor);
}

// Drains one source into one Class-B index, tracking a cursor + dead-letter for that index.
public sealed partial class AsyncVectorizationWorker
{
	readonly string _index;
	readonly ISearchSource _source;
	readonly ISearchIndex _target;
	readonly IIndexCursorStore _store;
	readonly int _maxAttempts;
	// Optional: without it a poison doc is dead-lettered in total silence — the index quietly
	// stops carrying that entity FOREVER and nobody ever learns why (the very hole this leaf closes).
	readonly ILogger? _log;

	public AsyncVectorizationWorker(
		string indexName, ISearchSource source, ISearchIndex target, IIndexCursorStore store, int maxAttempts = 5,
		ILogger? log = null)
	{
		_index = indexName;
		_source = source;
		_target = target;
		_store = store;
		_maxAttempts = maxAttempts;
		_log = log;
	}

	// One drain pass. Materializes the delta into the index; advances the cursor ONLY if nothing
	// is left in a transient-failure state. A failure that has burned through maxAttempts is
	// dead-lettered (skipped henceforth) so it stops blocking the cursor. Upserts/deletes are
	// idempotent in the index, so re-draining an un-advanced delta is safe.
	public async Task<DrainResult> DrainAsync(CancellationToken ct = default)
	{
		var cursor = await _store.GetCursorAsync(_index, ct);
		var delta = await _source.DeltaAsync(cursor, ct);

		var blocked = false;
		int indexed = 0, deleted = 0, deadLettered = 0;

		foreach (var del in delta.Deletes)
		{
			try { await _target.DeleteAsync(null, del.Scope, del.Type, del.Id, ct); deleted++; }
			catch (Exception ex)
			{
				blocked = true; // retried on the next drain (deletes aren't dead-lettered)
				if (_log is not null) LogDeleteFailed(_log, _index, del.Type, del.Id, ex);
			}
		}

		foreach (var doc in delta.Upserts)
		{
			if (await _store.IsDeadAsync(_index, doc.Type, doc.Id, ct)) continue;
			try
			{
				await _target.IndexAsync(null, doc, ct);
				await _store.ClearAttemptsAsync(_index, doc.Type, doc.Id, ct);
				indexed++;
			}
			catch (Exception ex)
			{
				var attempts = await _store.BumpAttemptsAsync(_index, doc.Type, doc.Id, ct);
				if (attempts >= _maxAttempts)
				{
					await _store.MarkDeadAsync(_index, doc.Type, doc.Id, ct);
					deadLettered++;
					// LOUD: this entity leaves the index permanently. The last exception's message
					// is the only evidence of WHY — it used to be swallowed here.
					if (_log is not null) LogDeadLetter(_log, _index, doc.Type, doc.Id, attempts, ex.Message, ex);
				}
				else
				{
					blocked = true; // transient → hold the cursor so a recovery backfills this item
					if (_log is not null) LogIndexAttemptFailed(_log, _index, doc.Type, doc.Id, attempts, ex.Message);
				}
			}
		}

		if (!blocked) await _store.SetCursorAsync(_index, delta.CurrentVersion, ct);
		var newCursor = await _store.GetCursorAsync(_index, ct);
		var result = new DrainResult(indexed, deleted, deadLettered, Advanced: !blocked, Cursor: newCursor,
			SourceVersion: delta.CurrentVersion);
		// Summary of the pass, including the LAG that says how far behind the semantic index is.
		// Skipped when there was literally nothing to do, so an idle drain loop stays quiet.
		if (_log is not null && (indexed > 0 || deleted > 0 || deadLettered > 0 || blocked))
			LogDrain(_log, _index, indexed, deleted, deadLettered, result.Advanced, newCursor, result.Lag);
		return result;
	}

	[LoggerMessage(EventId = 401, Level = LogLevel.Warning,
		Message = "vectorization {Index}: DEAD-LETTERED {Type}/{Id} after {Attempts} attempts — {Error}")]
	static partial void LogDeadLetter(ILogger logger, string index, string type, string id, int attempts, string error, Exception ex);

	[LoggerMessage(EventId = 402, Level = LogLevel.Debug,
		Message = "vectorization {Index}: {Type}/{Id} failed (attempt {Attempts}) — {Error}; cursor held")]
	static partial void LogIndexAttemptFailed(ILogger logger, string index, string type, string id, int attempts, string error);

	[LoggerMessage(EventId = 403, Level = LogLevel.Debug,
		Message = "vectorization {Index}: delete of {Type}/{Id} failed; cursor held")]
	static partial void LogDeleteFailed(ILogger logger, string index, string type, string id, Exception ex);

	[LoggerMessage(EventId = 404, Level = LogLevel.Information,
		Message = "vectorization {Index}: indexed {Indexed}, deleted {Deleted}, dead-lettered {DeadLettered}, advanced {Advanced}, cursor {Cursor}, lag {Lag}")]
	static partial void LogDrain(ILogger logger, string index, int indexed, int deleted, int deadLettered, bool advanced, long cursor, long lag);
}

// In-memory cursor store: fine for tests/dev and single-process drains. Not durable across
// restarts — the retrofit swaps in a SQLite-backed store co-located with the entity file.
public sealed class InMemoryIndexCursorStore : IIndexCursorStore
{
	readonly Dictionary<string, long> _cursors = new();
	readonly Dictionary<(string, string, string), int> _attempts = new();
	readonly HashSet<(string, string, string)> _dead = [];

	public Task<long> GetCursorAsync(string index, CancellationToken ct = default) =>
		Task.FromResult(_cursors.GetValueOrDefault(index));

	public Task SetCursorAsync(string index, long version, CancellationToken ct = default)
	{
		_cursors[index] = version;
		return Task.CompletedTask;
	}

	public Task<int> BumpAttemptsAsync(string index, string type, string id, CancellationToken ct = default)
	{
		var key = (index, type, id);
		var n = _attempts.GetValueOrDefault(key) + 1;
		_attempts[key] = n;
		return Task.FromResult(n);
	}

	public Task ClearAttemptsAsync(string index, string type, string id, CancellationToken ct = default)
	{
		_attempts.Remove((index, type, id));
		return Task.CompletedTask;
	}

	public Task MarkDeadAsync(string index, string type, string id, CancellationToken ct = default)
	{
		_dead.Add((index, type, id));
		return Task.CompletedTask;
	}

	public Task<bool> IsDeadAsync(string index, string type, string id, CancellationToken ct = default) =>
		Task.FromResult(_dead.Contains((index, type, id)));
}
