using LinqToDB;
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
public sealed class TasksVectorizationJob : IBackgroundIndexJob
{
	// Must match TasksService.VectorDim.
	const int VectorDim = 1024;

	readonly IScopedDbFactory<TasksDb> _factory;
	readonly IProjectCatalog _catalog;
	readonly ILlmClient? _llm;
	readonly ILogger<TasksVectorizationJob>? _logger;

	public TasksVectorizationJob(IScopedDbFactory<TasksDb> factory, IProjectCatalog catalog,
		ILlmClient? llm = null, ILogger<TasksVectorizationJob>? logger = null)
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
		foreach (var project in await _catalog.ListTaskProjectKeysAsync(ct))
		{
			ct.ThrowIfCancellationRequested();

			try
			{
				DataConnection Connect() => _factory.NewEnsuredConnection(project);

				List<string> boards;
				using (var probe = _factory.NewEnsuredConnection(project))
					boards = probe.GetTable<PlanNode>().Where(n => n.ActiveTo == null)
						.Select(n => n.Board).Distinct().ToList();

				foreach (var board in boards)
				{
					ct.ThrowIfCancellationRequested();
					var target = new VectorSearchIndex(Connect, new LlmClientEmbedder(_llm, project), VectorDim);
					var source = new TasksSearchSource(Connect, project, board);
					var cursor = new SqliteIndexCursorStore(Connect);
					var worker = new AsyncVectorizationWorker(board, source, target, cursor); // per-board cursor

					var r = await worker.DrainAsync(ct);
					indexed += r.Indexed;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One broken project file must not block the backfill of every other project
				// (spec: durable-backfill); it retries next tick.
				_logger?.LogError(ex, "tasks vectorization drain failed for {Project}; skipped", project);
			}
		}
		return indexed;
	}
}
