using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Web.Search;

// Drains every project's memory stores into the co-located Class-B vector index. Memory files are
// flat (memory/{project}.db, one file per project, all stores inside), so we enumerate the *.db
// files, and within each the distinct stores — stores are temporal PARTITIONS, so each store drains
// with its OWN cursor (MemoryCursors.Vector(store)) over its partition's delta. Mirrors
// TasksVectorizationJob. A down embedder dead-letters per item without head-of-line blocking
// (AsyncVectorizationWorker). No embedder wired → no-op.
//
// (Store enumeration reads the file's own rows; a follow-up card — W4 — moves the project/store
// enumeration of every memory job onto the MemoryStores catalog.)
public sealed class MemoryVectorizationJob : IBackgroundIndexJob
{
	// Must match MemoryService.VectorDim — the read path and the worker must store/query the same dim.
	const int VectorDim = 1024;

	readonly IScopedDbFactory<MemoryDb> _factory;
	readonly ILlmClient? _llm;
	readonly ILogger<MemoryVectorizationJob>? _logger;

	public MemoryVectorizationJob(IScopedDbFactory<MemoryDb> factory, ILlmClient? llm = null,
		ILogger<MemoryVectorizationJob>? logger = null)
	{
		_factory = factory;
		_llm = llm;
		_logger = logger;
	}

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var indexed = 0;
		foreach (var project in ScopedDbFiles.ListRootScopeKeys(_factory.BaseDir))
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				DataConnection Connect() => _factory.NewEnsuredConnection(project);

				List<string> stores;
				using (var probe = _factory.NewEnsuredConnection(project))
					stores = probe.Entries.Select(e => e.Store).Distinct().ToList();

				foreach (var store in stores)
				{
					ct.ThrowIfCancellationRequested();
					var target = new VectorSearchIndex(Connect, new LlmClientEmbedder(_llm, project), VectorDim);
					var source = new MemorySearchSource(Connect, project, store);
					var cursor = new SqliteIndexCursorStore(Connect);
					var worker = new AsyncVectorizationWorker(MemoryCursors.Vector(store), source, target, cursor);

					var r = await worker.DrainAsync(ct);
					indexed += r.Indexed;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One broken project file must not block the backfill of every other project
				// (spec: durable-backfill); it retries next tick.
				_logger?.LogError(ex, "memory vectorization drain failed for {Project}; skipped", project);
			}
		}
		return indexed;
	}
}
