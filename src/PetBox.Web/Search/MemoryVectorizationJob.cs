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
	private const int MaxDocsPerPass = 200;

	// work vectorization-jobs-flood-selflog: a healthy pass on a quiet project has nothing to say
	// (Indexed=0, DeadLettered=0, Lag=0) — that is not an Information-level fact, it is silence. The
	// per-project stats line is still emitted every tick (below), just gated to Debug when there is
	// no signal, so the global self-logging MinimumLevel=Information (SystemLoggerOptions) drops it.
	// Complete silence would make a dead job indistinguishable from an idle one, so one heartbeat
	// line per hour survives at Information regardless of signal. The unit of the heartbeat is the
	// JOB'S PASS, not the project: DrainAllAsync visits every project on every 60s tick, so a
	// per-project heartbeat would itself be the same N-events-per-tick multiplier this fix removes.
	// One line per hour, summarizing the most recent pass, is enough to prove liveness.
	static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(1);

	// Process-lifetime state, not per-instance: SearchEnrichmentService.RunOncePassAsync opens a
	// FRESH DI scope every tick (`using var scope = _services.CreateScope()`), so a new
	// MemoryVectorizationJob is constructed each pass — an instance field would forget the last
	// heartbeat before the next tick ever ran. A restart resets this to null, which is the correct
	// behavior (not a bug to guard against): the first pass after a restart heartbeats immediately
	// (proving the job came back up), then the hourly cadence resumes — no burst, because
	// DrainAllAsync (and so this check) still runs at most once per 60s tick.
	static DateTimeOffset? s_lastHeartbeatUtc;
	static readonly Lock s_heartbeatLock = new();

	readonly IScopedDbFactory<MemoryDb> _factory;
	readonly IProjectCatalog _catalog;
	readonly ILlmClient? _llm;
	readonly ILogger<MemoryVectorizationJob>? _logger;
	readonly TimeProvider _time;

	public MemoryVectorizationJob(IScopedDbFactory<MemoryDb> factory, IProjectCatalog catalog,
		ILlmClient? llm = null, ILogger<MemoryVectorizationJob>? logger = null, TimeProvider? time = null)
	{
		_factory = factory;
		_catalog = catalog;
		_llm = llm;
		_logger = logger;
		_time = time ?? TimeProvider.System;
	}

	// Test-only: the heartbeat clock is process-lifetime static state (see s_lastHeartbeatUtc above),
	// so tests that assert its behavior need to isolate themselves from whatever a previous test in
	// the same process left behind. Internal via PetBox.Web's InternalsVisibleTo(PetBox.Tests).
	internal static void ResetHeartbeatClockForTests() { lock (s_heartbeatLock) s_lastHeartbeatUtc = null; }

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var indexed = 0;
		var budget = MaxDocsPerPass; // per-PASS embed budget, shared by every project/store below
		var passProjects = 0; var passStores = 0; var passDead = 0; long passMaxLag = 0; // heartbeat rollup
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
				// work vectorization-jobs-flood-selflog: gated to Information ONLY when there is a
				// signal (Indexed>0 or DeadLettered>0 or Lag>0) — an empty pass ("nothing happened")
				// goes to Debug, which the self-log's global MinimumLevel=Information then drops. This
				// was 93% of this job's self-log volume (576149/617446 events over the 2026-08-27
				// measurement window — log_query, SourceContext=MemoryVectorizationJob, EventId=410).
				if (_logger is not null && stores.Count > 0)
				{
					using var stats = _factory.NewEnsuredConnection(project);
					var (vectors, dead) = await SearchIndexStatsReader.ReadAsync(stats, ct);
					var hasSignal = projectIndexed > 0 || projectDead > 0 || maxLag > 0;
					LogProjectStats(_logger, hasSignal ? LogLevel.Information : LogLevel.Debug,
						project, stores.Count, projectIndexed, projectDead, vectors, dead, maxLag);
					passProjects++;
					passStores += stores.Count;
					passDead += projectDead;
					passMaxLag = Math.Max(passMaxLag, maxLag);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One broken project file must not block the backfill of every other project
				// (spec: durable-backfill); it retries next tick.
				_logger?.LogError(ex, "memory vectorization drain failed for {Project}; skipped", project);
			}
		}
		MaybeLogHeartbeat(passProjects, passStores, indexed, passDead, passMaxLag);
		return indexed;
	}

	// One Information line per hour regardless of signal, so a totally silent (gated-to-Debug)
	// stretch stays distinguishable from a dead job. Summarizes the MOST RECENT pass, not a
	// rolling hour total — good enough to prove "the job ran and this is what it saw", which is
	// the only thing a liveness heartbeat needs to say.
	void MaybeLogHeartbeat(int projects, int stores, int indexed, int deadLettered, long maxLag)
	{
		if (_logger is null) return;
		var now = _time.GetUtcNow();
		lock (s_heartbeatLock)
		{
			if (s_lastHeartbeatUtc is { } last && now - last < HeartbeatInterval) return;
			s_lastHeartbeatUtc = now;
		}
		LogHeartbeat(_logger, projects, stores, indexed, deadLettered, maxLag);
	}

	[LoggerMessage(EventId = 412, Level = LogLevel.Information,
		Message = "memory vectorization {Project}: Embed unavailable (no route or circuit open) — skipping this pass, cursor untouched")]
	static partial void LogEmbedUnavailable(ILogger logger, string project);

	[LoggerMessage(EventId = 410,
		Message = "memory vectorization {Project}: {Stores} store(s), indexed {Indexed}, dead-lettered {DeadLettered} this pass; search_vec rows {VectorRows}, dead total {DeadTotal}, max cursor lag {Lag}")]
	static partial void LogProjectStats(ILogger logger, LogLevel level, string project, int stores, int indexed,
		int deadLettered, long vectorRows, long deadTotal, long lag);

	[LoggerMessage(EventId = 415, Level = LogLevel.Information,
		Message = "memory vectorization heartbeat: job alive; last pass touched {Projects} project(s), {Stores} store(s), indexed {Indexed}, dead-lettered {DeadLettered} this pass, max cursor lag {Lag}")]
	static partial void LogHeartbeat(ILogger logger, int projects, int stores, int indexed, int deadLettered, long lag);
}
