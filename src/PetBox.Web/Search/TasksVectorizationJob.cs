using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Web.Search;

// Drains each project's board vectors into the co-located Class-B index. Tasks files are flat
// (tasks/{project}.db, one file per project, all boards inside); the project list comes from the
// TaskBoards CATALOG (core.db), and within each file the distinct boards — boards are temporal
// PARTITIONS, so each board drains with its OWN cursor (IndexName = board) over its partition's
// delta. A down embedder dead-letters per item without head-of-line blocking. No embedder wired →
// no-op.
//
// Catalog, not file scan (spec: catalog-is-source-of-truth). The tasks file is created lazily on
// first node write, so `tasks/*.db` is not the project list: it MISSES a project whose board exists
// in the catalog but whose file was never materialized, and it keeps draining the GHOST file of a
// deleted project until TaskBoardOrphanCleanupService reclaims it. `TaskBoards` is the tasks tier's
// own catalog (written with the board, cascaded on project delete).
//
// Lazy-creation: the list is exactly the projects that already own a board, so opening the file
// (NewEnsuredConnection → schema ensure) materializes it only where the catalog says it belongs —
// migrations then run here, under supervision, rather than at some random first write. A project
// with no board is not in the list and gets no empty file.
public sealed partial class TasksVectorizationJob : IBackgroundIndexJob
{
	// Must match TasksService.VectorDim.
	const int VectorDim = 1024;

	// Per-PASS embed budget across every project and board — same reasoning (and same number) as
	// MemoryVectorizationJob.MaxDocsPerPass: one sequential HTTP embed per doc (~150 ms), jobs run
	// serially on a 60s enrichment tick, so a post-reindex delta (the whole board) must be drained
	// in portions or it owns the tick. 200 docs ≈ 30 s.
	private const int MaxDocsPerPass = 200;

	// work vectorization-jobs-flood-selflog: mirrors MemoryVectorizationJob's heartbeat rationale —
	// see that file for the full writeup. Summary: an empty pass is gated to Debug (below), and one
	// Information heartbeat line per hour proves liveness without reintroducing a per-project/per-tick
	// multiplier. The heartbeat is per JOB PASS, not per project, for the same reason.
	static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(1);

	// Process-lifetime state: SearchEnrichmentService opens a fresh DI scope every 60s tick, so a new
	// TasksVectorizationJob instance is constructed each pass — only a static field survives across
	// ticks. Resets to null on restart, which correctly heartbeats once immediately, then resumes the
	// hourly cadence (no burst: DrainAllAsync still runs at most once per tick).
	static DateTimeOffset? s_lastHeartbeatUtc;
	static readonly Lock s_heartbeatLock = new();

	readonly IScopedDbFactory<TasksDb> _factory;
	readonly IProjectCatalog _catalog;
	readonly ILlmClient? _llm;
	readonly ILogger<TasksVectorizationJob>? _logger;
	readonly TimeProvider _time;

	public TasksVectorizationJob(IScopedDbFactory<TasksDb> factory, IProjectCatalog catalog,
		ILlmClient? llm = null, ILogger<TasksVectorizationJob>? logger = null, TimeProvider? time = null)
	{
		_factory = factory;
		_catalog = catalog;
		_llm = llm;
		_logger = logger;
		_time = time ?? TimeProvider.System;
	}

	// Test-only: see MemoryVectorizationJob.ResetHeartbeatClockForTests for why this exists. Internal
	// via PetBox.Web's InternalsVisibleTo(PetBox.Tests).
	internal static void ResetHeartbeatClockForTests() { lock (s_heartbeatLock) s_lastHeartbeatUtc = null; }

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null) return 0;

		var indexed = 0;
		var budget = MaxDocsPerPass; // per-PASS embed budget, shared by every project/board below
		var passProjects = 0; var passBoards = 0; var passDead = 0; long passMaxLag = 0; // heartbeat rollup
		foreach (var project in await _catalog.ListTaskProjectKeysAsync(ct))
		{
			if (budget <= 0) break; // spent — the remaining backlog drains on the next tick
			ct.ThrowIfCancellationRequested();

			try
			{
				// Gate on Embed being reachable for THIS project (no route / breaker open) before the
				// drain touches a single doc — same gate the chat jobs already have. A down endpoint is
				// a normal, self-healing state (Info, not Warning): skip the tick, keep the cursor. This
				// is the difference between "the endpoint is down for 5 minutes" and "every document in
				// the project is permanently dead-lettered". The worker's infra classification covers
				// the endpoint dying MID-pass, after this gate said yes.
				if (!await _llm.IsAvailableAsync(project, LlmCapability.Embed, ct))
				{
					if (_logger is not null) LogEmbedUnavailable(_logger, project);
					continue;
				}

				DataConnection Connect() => _factory.NewEnsuredConnection(project);

				// No ActiveTo filter: a board with every node soft-deleted still owes its
				// deletions to the index and must keep draining until the delta is empty —
				// same shape as MemoryVectorizationJob's store enumeration below.
				List<string> boards;
				using (var probe = _factory.NewEnsuredConnection(project))
					boards = probe.GetTable<TaskNode>()
						.Select(n => n.Board).Distinct().ToList();

				int projectIndexed = 0, projectDead = 0;
				long maxLag = 0;
				foreach (var board in boards)
				{
					if (budget <= 0) break;
					ct.ThrowIfCancellationRequested();
					var target = new VectorSearchIndex(Connect, new LlmClientEmbedder(_llm, project), VectorDim);
					var source = new TasksSearchSource(Connect, project, board, maxDocs: budget);
					var cursor = new SqliteIndexCursorStore(Connect);
					var worker = new AsyncVectorizationWorker(board, source, target, cursor, log: _logger); // per-board cursor

					var r = await worker.DrainAsync(ct);
					budget -= r.Indexed;
					indexed += r.Indexed;
					projectIndexed += r.Indexed;
					projectDead += r.DeadLettered; // previously dropped: a dead-lettered node vanished in silence
					maxLag = Math.Max(maxLag, r.Lag);
				}

				// Same three counters as the memory job: vectors present, docs permanently dropped,
				// how far the cursor trails the boards' version space (0 vectors + boards ⇒ dead index).
				// work vectorization-jobs-flood-selflog: gated to Information ONLY when there is a signal
				// (Indexed>0 or DeadLettered>0 or Lag>0) — an empty pass goes to Debug, which the
				// self-log's global MinimumLevel=Information then drops. This was 91% of this job's
				// self-log volume (405974/446135 events over the 2026-08-27 measurement window —
				// log_query, SourceContext=TasksVectorizationJob, EventId=411).
				if (_logger is not null && boards.Count > 0)
				{
					using var stats = _factory.NewEnsuredConnection(project);
					var (vectors, dead) = await SearchIndexStatsReader.ReadAsync(stats, ct);
					var hasSignal = projectIndexed > 0 || projectDead > 0 || maxLag > 0;
					LogProjectStats(_logger, hasSignal ? LogLevel.Information : LogLevel.Debug,
						project, boards.Count, projectIndexed, projectDead, vectors, dead, maxLag);
					passProjects++;
					passBoards += boards.Count;
					passDead += projectDead;
					passMaxLag = Math.Max(passMaxLag, maxLag);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One broken project file must not block the backfill of every other project
				// (spec: durable-backfill); it retries next tick.
				_logger?.LogError(ex, "tasks vectorization drain failed for {Project}; skipped", project);
			}
		}
		MaybeLogHeartbeat(passProjects, passBoards, indexed, passDead, passMaxLag);
		return indexed;
	}

	// One Information line per hour regardless of signal — see MemoryVectorizationJob.MaybeLogHeartbeat
	// for the full rationale (identical here, board/project vocabulary aside).
	void MaybeLogHeartbeat(int projects, int boards, int indexed, int deadLettered, long maxLag)
	{
		if (_logger is null) return;
		var now = _time.GetUtcNow();
		lock (s_heartbeatLock)
		{
			if (s_lastHeartbeatUtc is { } last && now - last < HeartbeatInterval) return;
			s_lastHeartbeatUtc = now;
		}
		LogHeartbeat(_logger, projects, boards, indexed, deadLettered, maxLag);
	}

	[LoggerMessage(EventId = 413, Level = LogLevel.Information,
		Message = "tasks vectorization {Project}: Embed unavailable (no route or circuit open) — skipping this pass, cursor untouched")]
	static partial void LogEmbedUnavailable(ILogger logger, string project);

	[LoggerMessage(EventId = 411,
		Message = "tasks vectorization {Project}: {Boards} board(s), indexed {Indexed}, dead-lettered {DeadLettered} this pass; search_vec rows {VectorRows}, dead total {DeadTotal}, max cursor lag {Lag}")]
	static partial void LogProjectStats(ILogger logger, LogLevel level, string project, int boards, int indexed,
		int deadLettered, long vectorRows, long deadTotal, long lag);

	[LoggerMessage(EventId = 416, Level = LogLevel.Information,
		Message = "tasks vectorization heartbeat: job alive; last pass touched {Projects} project(s), {Boards} board(s), indexed {Indexed}, dead-lettered {DeadLettered} this pass, max cursor lag {Lag}")]
	static partial void LogHeartbeat(ILogger logger, int projects, int boards, int indexed, int deadLettered, long lag);
}
