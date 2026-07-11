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
public sealed partial class MemoryVectorizationJob : IBackgroundIndexJob
{
	// Must match MemoryService.VectorDim — the read path and the worker must store/query the same dim.
	const int VectorDim = 1024;

	// How many documents ONE pass of this job may embed, across every project and store it walks.
	// Embedding is one sequential HTTP call per doc (~150 ms against the home endpoint), and
	// SearchEnrichmentService runs its jobs one after another on a 60s tick — so an uncapped pass
	// after a reindex (delta = the whole store) would hold the tick for the entire backfill and
	// starve the digest/facts/behavior jobs behind it. 200 docs ≈ 30 s ≈ half the tick: steady-state
	// deltas (a handful of docs) are unaffected, and a backfill drains in portions, one per tick.
	// The budget is spent in catalog order, so a big project can eat a whole pass; it simply
	// continues on the next tick, and the projects behind it start moving once it is caught up.
	internal const int MaxDocsPerPass = 200;

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
		var budget = MaxDocsPerPass; // per-PASS embed budget, shared by every project/store below
		foreach (var project in await _catalog.ListMemoryProjectKeysAsync(ct))
		{
			if (budget <= 0) break; // out of budget — the rest of the backlog is next tick's
			ct.ThrowIfCancellationRequested();
			try
			{
				// Gate on Embed being reachable for THIS project (no route / breaker open) before the
				// drain touches a single doc — same gate the chat jobs already have. A down endpoint is
				// a normal, self-healing state (Info, not Warning): we simply skip this tick. Without it
				// the drain used to walk every document into a dead socket, and the failures looked like
				// the DOCUMENTS' fault. Belt-and-braces with the worker's own infra classification: the
				// endpoint can also die mid-pass, after the gate said yes.
				if (!await _llm.IsAvailableAsync(project, LlmCapability.Embed, ct))
				{
					if (_logger is not null) LogEmbedUnavailable(_logger, project);
					continue;
				}

				DataConnection Connect() => _factory.NewEnsuredConnection(project);

				List<string> stores;
				using (var probe = _factory.NewEnsuredConnection(project))
					stores = probe.Entries.Select(e => e.Store).Distinct().ToList();

				int projectIndexed = 0, projectDead = 0;
				long maxLag = 0;
				foreach (var store in stores)
				{
					if (budget <= 0) break;
					ct.ThrowIfCancellationRequested();
					var target = new VectorSearchIndex(Connect, new LlmClientEmbedder(_llm, project), VectorDim);
					var source = new MemorySearchSource(Connect, project, store, maxDocs: budget);
					var cursor = new SqliteIndexCursorStore(Connect);
					var worker = new AsyncVectorizationWorker(MemoryCursors.Vector(store), source, target, cursor,
						log: _logger);

					var r = await worker.DrainAsync(ct);
					budget -= r.Indexed;
					indexed += r.Indexed;
					projectIndexed += r.Indexed;
					projectDead += r.DeadLettered; // used to be dropped on the floor — a dead-letter was invisible
					maxLag = Math.Max(maxLag, r.Lag);
				}

				// The three numbers that make a dead semantic index visible on day one: how many
				// vectors this project actually has (0 with entries present ⇒ it NEVER ran), how
				// many docs were permanently dropped, and how far the cursor trails the data.
				// Logged (the existing observability pipeline) — no new metric mechanism invented.
				if (_logger is not null && stores.Count > 0)
				{
					using var stats = _factory.NewEnsuredConnection(project);
					var (vectors, dead) = await SearchIndexStatsReader.ReadAsync(stats, ct);
					LogProjectStats(_logger, project, stores.Count, projectIndexed, projectDead, vectors, dead, maxLag);
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

	[LoggerMessage(EventId = 412, Level = LogLevel.Information,
		Message = "memory vectorization {Project}: Embed unavailable (no route or circuit open) — skipping this pass, cursor untouched")]
	static partial void LogEmbedUnavailable(ILogger logger, string project);

	[LoggerMessage(EventId = 410, Level = LogLevel.Information,
		Message = "memory vectorization {Project}: {Stores} store(s), indexed {Indexed}, dead-lettered {DeadLettered} this pass; search_vec rows {VectorRows}, dead total {DeadTotal}, max cursor lag {Lag}")]
	static partial void LogProjectStats(ILogger logger, string project, int stores, int indexed, int deadLettered,
		long vectorRows, long deadTotal, long lag);
}
