using LinqToDB.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Search;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services;

// ISearchSource over ONE board's slice of a project plan file: the async-vectorization worker rides
// it to materialize that board's vectors off the write path. Boards are temporal partitions, so the
// delta (and the worker's cursor) is per-board (partition n.Board == board). A node that left the
// open set (became terminal) OR was removed/renamed-away is emitted as a Delete so its vector is
// dropped — search covers only the open set. Tags aren't embedded (Class-B uses Name+Body only), so
// the doc carries none here. Fresh connection per drain (the worker runs outside the request scope).
public sealed class TasksSearchSource : ISearchSource
{
	readonly Func<DataConnection> _connect;
	readonly string _scope;
	readonly string _board;

	public TasksSearchSource(Func<DataConnection> connect, string scope, string board)
	{
		_connect = connect;
		_scope = scope;
		_board = board;
	}

	public async Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default)
	{
		using var db = _connect();
		var (added, updated, removed, current) =
			await TemporalStore.ChangesSinceAsync<PlanNode>(db, sinceVersion, n => n.Board == _board, ct);
		var changed = added.Concat(updated).ToList();

		var upserts = changed.Where(TasksSearchDocs.IsIndexable)
			.Select(n => TasksSearchDocs.ToDoc(n, _scope, []))
			.ToList();
		var deletes = removed.Select(k => new DocRef(_scope, _board, k))
			.Concat(changed.Where(n => !TasksSearchDocs.IsIndexable(n)).Select(n => new DocRef(_scope, _board, n.Key)))
			.ToList();
		return new SourceDelta(upserts, deletes, current);
	}
}
