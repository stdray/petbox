using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Web.Search;

// Drains every project's memory stores into the co-located Class-B vector index. Memory files are
// flat (memory/{project}.db, one file per project, all stores inside); the project list comes from
// the MemoryStores CATALOG (core.db), and within each file the distinct stores — stores are
// temporal PARTITIONS, so each store drains with its OWN cursor (MemoryCursors.Vector(store)) over
// its partition's delta. Mirrors TasksVectorizationJob. A down embedder dead-letters per item
// without head-of-line blocking (AsyncVectorizationWorker). No embedder wired → no-op.
//
// Catalog, not file scan (spec: catalog-is-source-of-truth). The memory file is created lazily on
// first write, so `memory/*.db` is not the project list: it MISSES a project whose store exists in
// the catalog but whose file has not been materialized yet, and it keeps a GHOST — a deleted
// project's file, which lingers until MemoryOrphanCleanupService reclaims it — alive, burning embed
// calls on a project that no longer exists. `MemoryStores` is memory's own catalog (a row is
// written on explicit create AND on the auto-vivifying first write, and cascaded on project
// delete), so it is both narrower and truer than the disk.
//
// Lazy-creation: drained projects are exactly those that ALREADY have memory, so opening the file
// (NewEnsuredConnection → schema ensure) materializes it only for a project whose store row says it
// should exist — the migration then runs here, under supervision, instead of at a random first
// write. Projects that never touched memory are not in the list and get no empty file.
public sealed class MemoryVectorizationJob : IBackgroundIndexJob
{
	// Must match MemoryService.VectorDim — the read path and the worker must store/query the same dim.
	const int VectorDim = 1024;

	readonly IScopedDbFactory<MemoryDb> _factory;
	readonly IProjectCatalog _catalog;
	readonly ILlmClient? _llm;
	readonly ILogger<MemoryVectorizationJob>? _logger;

	public MemoryVectorizationJob(IScopedDbFactory<MemoryDb> factory, IProjectCatalog catalog,
		ILlmClient? llm = null, ILogger<MemoryVectorizationJob>? logger = null)
	{
		_factory = factory;
		_catalog = catalog;
		_llm = llm;
		_logger = logger;
	}

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var indexed = 0;
		foreach (var project in await _catalog.ListMemoryProjectKeysAsync(ct))
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
