using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Web.Search;

// Drains each project's board vectors into the co-located Class-B index. Tasks files are flat
// (tasks/{project}.db, one file per project, all boards inside), so we enumerate the *.db files,
// and within each the distinct boards — boards are temporal PARTITIONS, so each board drains with
// its OWN cursor (IndexName = board) over its partition's delta. A down embedder dead-letters per
// item without head-of-line blocking. No embedder wired → no-op.
public sealed class TasksVectorizationJob : IVectorizationJob
{
	// Must match TasksService.VectorDim.
	const int VectorDim = 1024;

	readonly IScopedDbFactory<TasksDb> _factory;
	readonly ILlmClient? _llm;

	public TasksVectorizationJob(IScopedDbFactory<TasksDb> factory, ILlmClient? llm = null)
	{
		_factory = factory;
		_llm = llm;
	}

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		if (_llm is null || !Directory.Exists(_factory.BaseDir)) return 0;

		var indexed = 0;
		foreach (var path in Directory.EnumerateFiles(_factory.BaseDir, "*.db"))
		{
			var project = Path.GetFileNameWithoutExtension(path);
			if (string.IsNullOrEmpty(project)) continue;
			ct.ThrowIfCancellationRequested();

			DataConnection Connect() => _factory.NewConnection(project);

			List<string> boards;
			using (var probe = _factory.NewConnection(project))
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
		return indexed;
	}
}
