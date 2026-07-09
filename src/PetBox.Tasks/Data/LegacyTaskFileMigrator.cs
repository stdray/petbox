using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data;

// One-time migration from the legacy one-file-per-board layout (tasks/<project>/<board>.db)
// to the one-file-per-project layout (tasks/<project>.db, boards partitioned by Board).
//
// Zero-loss + idempotent: each legacy board file is brought to the current schema, ALL its
// revisions (active + history) are copied into the project file with Board stamped, the row
// count is reconciled, and only then is the original RENAMED to "<board>.db.migrated"
// (kept, not deleted, for recovery). A rerun skips already-migrated files; a count mismatch
// or any error aborts that one board (leaving its original untouched) and is logged.
public sealed class LegacyTaskFileMigrator
{
	readonly string _tasksDir;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly ILogger? _log;

	public LegacyTaskFileMigrator(string tasksDir, IScopedDbFactory<TasksDb> factory, ILogger? log = null)
	{
		_tasksDir = tasksDir;
		_factory = factory;
		_log = log;
	}

	// Returns the number of board files migrated this run.
	public int Migrate()
	{
		if (!Directory.Exists(_tasksDir)) return 0;

		var migrated = 0;
		foreach (var projectDir in Directory.GetDirectories(_tasksDir))
		{
			var project = Path.GetFileName(projectDir);
			var boardFiles = Directory.GetFiles(projectDir, "*.db");
			if (boardFiles.Length == 0) continue;

			using var projectDb = _factory.GetDb(project); // ensures the per-project schema (M001..M005)
			foreach (var boardFile in boardFiles)
			{
				var board = Path.GetFileNameWithoutExtension(boardFile);
				try
				{
					if (MigrateBoard(projectDb, boardFile, board)) migrated++;
				}
				catch (Exception ex)
				{
					_log?.LogError(ex, "Tasks legacy migration failed for {Project}/{Board}; original left in place", project, board);
				}
			}
		}
		return migrated;
	}

	bool MigrateBoard(TasksDb projectDb, string boardFile, string board)
	{
		// Defensive idempotency: if the project file already holds this board's rows (a prior
		// run got that far), don't copy again — just mark the legacy file done.
		if (projectDb.PlanNodes.Any(n => n.Board == board))
		{
			MarkMigrated(boardFile);
			return false;
		}

		// Work on a PRIVATE COPY so the original is never modified and no two processes ever
		// migrate the same file (a FluentMigrator VersionInfo race). Ensure() upgrades ANY
		// legacy schema to the current one (M001..M005: adds Type/NodeId/Board, remaps an
		// integer Status to its slug), so even very old board files migrate. Pooling=False
		// releases handles immediately so the temp can be deleted and the rename can't block.
		var temp = Path.Combine(Path.GetTempPath(), $"petbox-legacy-{Guid.NewGuid():N}.db");
		CopyWithSidecars(boardFile, temp);
		try
		{
			var tempCs = $"Data Source={temp};Pooling=False";
			TasksSchema.Ensure(tempCs);

			List<PlanNode> rows;
			using (var legacy = new TasksDb(TasksDb.CreateOptions(tempCs)))
				rows = legacy.PlanNodes.ToList();

			foreach (var r in rows)
				projectDb.Insert(r with { Board = board }); // preserves all temporal columns

			var copied = projectDb.PlanNodes.Count(n => n.Board == board);
			if (copied != rows.Count)
				throw new InvalidOperationException($"row-count mismatch for board '{board}': {rows.Count} legacy vs {copied} copied");

			_log?.LogInformation("Tasks: migrated board '{Board}' ({Rows} rows) into the per-project file", board, rows.Count);
		}
		finally
		{
			DeleteWithSidecars(temp);
		}

		MarkMigrated(boardFile);
		return true;
	}

	// SQLite keeps -wal/-shm sidecars next to the .db; copy them too so a not-yet-checkpointed
	// legacy file migrates without losing its tail.
	static void CopyWithSidecars(string src, string dst)
	{
		File.Copy(src, dst, overwrite: true);
		foreach (var ext in new[] { "-wal", "-shm" })
			if (File.Exists(src + ext)) File.Copy(src + ext, dst + ext, overwrite: true);
	}

	static void DeleteWithSidecars(string path)
	{
		foreach (var p in new[] { path, path + "-wal", path + "-shm" })
			try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
	}

	static void MarkMigrated(string boardFile)
	{
		var dest = boardFile + ".migrated";
		if (File.Exists(dest)) File.Delete(dest);
		File.Move(boardFile, dest);
	}
}
