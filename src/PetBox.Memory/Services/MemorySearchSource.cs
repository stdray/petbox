using LinqToDB.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Search;
using PetBox.Memory.Data;

namespace PetBox.Memory.Services;

// ISearchSource over ONE store's slice of a project memory file: the async-vectorization worker
// rides it to subscribe to that store's temporal log (TemporalStore.ChangesSinceAsync) and
// materialize vectors off the write path. Stores are temporal PARTITIONS, so the delta — and the
// worker's cursor (MemoryCursors.Vector(store)) — is per-store, exactly like the tasks tier's
// per-board source. No outbox table: the cursor IS the subscription. A fresh connection per drain
// (the worker runs outside the request scope). Scope is the project key, matching the read path.
public sealed class MemorySearchSource : ISearchSource
{
	readonly Func<DataConnection> _connect;
	readonly string _scope;
	readonly string _store;
	readonly int _maxDocs;

	// maxDocs > 0 caps ONE drain at that many documents (SearchDeltaCap): the delta is a
	// version-aligned PREFIX of the changes and its CurrentVersion is that prefix's watermark, so
	// the cursor advances partway and the next drain continues. Matters after a reindex, where the
	// delta is the entire store and an uncapped pass would hold the 60s enrichment tick hostage.
	// 0 = unlimited (the plain incremental case, and the tests that drain a handful of docs).
	public MemorySearchSource(Func<DataConnection> connect, string scope, string store, int maxDocs = 0)
	{
		_connect = connect;
		_scope = scope;
		_store = store;
		_maxDocs = maxDocs;
	}

	public async Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default)
	{
		using var db = _connect();
		var (added, updated, removed, current) =
			await TemporalStore.ChangesSinceAsync<MemoryEntry>(db, sinceVersion, e => e.Store == _store, ct);
		var (rows, watermark) = SearchDeltaCap.Take(added.Concat(updated), current, _maxDocs);
		var upserts = rows.Select(e => MemorySearchDocs.ToDoc(e, _scope)).ToList();
		// Deletes are free (no embed call) and idempotent, so they all ride along even when the
		// upserts were cut short — a vector whose entry is gone should not linger for another tick.
		var deletes = removed.Select(k => new DocRef(_scope, _store, k)).ToList();
		return new SourceDelta(upserts, deletes, watermark);
	}
}
