using LinqToDB.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Search;
using PetBox.Memory.Data;

namespace PetBox.Memory.Services;

// ISearchSource over one memory store file: the async-vectorization worker rides it to subscribe to
// the store's temporal log (TemporalStore.ChangesSinceAsync) and materialize vectors off the write
// path. No outbox table — the cursor IS the subscription. A fresh connection per drain (the worker
// runs outside the request scope). Scope is the project key, matching the read path's scope.
public sealed class MemorySearchSource : ISearchSource
{
	readonly Func<DataConnection> _connect;
	readonly string _scope;

	public MemorySearchSource(Func<DataConnection> connect, string scope)
	{
		_connect = connect;
		_scope = scope;
	}

	public async Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default)
	{
		using var db = _connect();
		var (added, updated, removed, current) =
			await TemporalStore.ChangesSinceAsync<MemoryEntry>(db, sinceVersion, ct: ct);
		var upserts = added.Concat(updated).Select(e => MemorySearchDocs.ToDoc(e, _scope)).ToList();
		var deletes = removed.Select(k => new DocRef(_scope, MemorySearchDocs.Type, k)).ToList();
		return new SourceDelta(upserts, deletes, current);
	}
}
