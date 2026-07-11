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

	public MemorySearchSource(Func<DataConnection> connect, string scope, string store)
	{
		_connect = connect;
		_scope = scope;
		_store = store;
	}

	public async Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default)
	{
		using var db = _connect();
		var (added, updated, removed, current) =
			await TemporalStore.ChangesSinceAsync<MemoryEntry>(db, sinceVersion, e => e.Store == _store, ct);
		var upserts = added.Concat(updated).Select(e => MemorySearchDocs.ToDoc(e, _scope)).ToList();
		var deletes = removed.Select(k => new DocRef(_scope, _store, k)).ToList();
		return new SourceDelta(upserts, deletes, current);
	}
}
