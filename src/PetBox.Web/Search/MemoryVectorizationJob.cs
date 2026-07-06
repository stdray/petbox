using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Web.Search;

// Drains every memory store file into its co-located Class-B vector index. Each (project, store)
// file gets its own cursor (one vector index per file), so a fresh embedder backfills only what
// changed; on first run the cursor is 0 → full re-embed. A down embedder dead-letters per item
// without head-of-line blocking (AsyncVectorizationWorker). No embedder wired → no-op.
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
		foreach (var project in ScopedDbFiles.ListScopeKeys(_factory.BaseDir))
		{
			foreach (var store in ScopedDbFiles.ListNames(_factory.BaseDir, project))
			{
				ct.ThrowIfCancellationRequested();
				try
				{
					// The drain runs on raw NewConnection()s, which skip _ensureSchema — a store
					// file last opened before the search-tables migration has no search_cursor.
					// GetDb runs the migrations (cached per file), so the drain sees current schema.
					_factory.GetDb(project, store);

					Func<DataConnection> connect = () => _factory.NewConnection(project, store);
					var target = new VectorSearchIndex(connect, new LlmClientEmbedder(_llm, project), VectorDim);
					var source = new MemorySearchSource(connect, project);
					var cursor = new SqliteIndexCursorStore(connect);
					var worker = new AsyncVectorizationWorker(MemorySearchDocs.VectorIndex, source, target, cursor);

					var r = await worker.DrainAsync(ct);
					indexed += r.Indexed;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// One broken store must not block the backfill of every other store
					// (spec: durable-backfill); it retries next tick.
					_logger?.LogError(ex, "memory vectorization drain failed for {Project}/{Store}; skipped", project, store);
				}
			}
		}
		return indexed;
	}
}
