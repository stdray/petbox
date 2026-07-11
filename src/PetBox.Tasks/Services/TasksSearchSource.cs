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
	readonly int _maxDocs;

	// maxDocs > 0 caps ONE drain at that many changed nodes (SearchDeltaCap): the delta is a
	// version-aligned PREFIX and its CurrentVersion is that prefix's watermark, so the cursor
	// advances partway and the next drain picks up from there. Matters after a reindex, whose first
	// delta is the whole board — uncapped, that one pass would own the 60s enrichment tick.
	// 0 = unlimited (plain incremental drains, and tests).
	public TasksSearchSource(Func<DataConnection> connect, string scope, string board, int maxDocs = 0)
	{
		_connect = connect;
		_scope = scope;
		_board = board;
		_maxDocs = maxDocs;
	}

	public async Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default)
	{
		using var db = _connect();
		var (added, updated, removed, current) =
			await TemporalStore.ChangesSinceAsync<PlanNode>(db, sinceVersion, n => n.Board == _board, ct);
		var (changed, watermark) = SearchDeltaCap.Take(added.Concat(updated), current, _maxDocs);

		var upserts = changed.Where(TasksSearchDocs.IsIndexable)
			.Select(n => TasksSearchDocs.ToDoc(n, _scope, []))
			.ToList();
		// Deletes cost no embed call, so the whole removed set rides along even under a cap; the
		// nodes that went terminal beyond the watermark are re-emitted next pass (deletes are
		// idempotent), when the cursor reaches them.
		var deletes = removed.Select(k => new DocRef(_scope, _board, k))
			.Concat(changed.Where(n => !TasksSearchDocs.IsIndexable(n)).Select(n => new DocRef(_scope, _board, n.Key)))
			.ToList();
		return new SourceDelta(upserts, deletes, watermark);
	}
}
