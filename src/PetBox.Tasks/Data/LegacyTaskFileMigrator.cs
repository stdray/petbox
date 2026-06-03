using LinqToDB;
using LinqToDB.Data;
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

			var projectDb = _factory.GetDb(project); // ensures the per-project schema (M001..M005)
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

		// Read the legacy file READ-ONLY (Mode=ReadOnly) — never migrate or write it, so we
		// don't run FluentMigrator on it (its VersionInfo would race across parallel boots).
		// Pooling=False releases the handle immediately so the rename can't be blocked. The
		// explicit column list (with a literal '' AS Board) reads any post-M002/M003/M004 file
		// without depending on a Board column the legacy file doesn't have.
		var oldCs = $"Data Source={boardFile};Pooling=False;Mode=ReadOnly";
		List<PlanNode> rows;
		using (var legacy = new LinqToDB.Data.DataConnection(TasksDb.CreateOptions(oldCs).Options))
			rows = legacy.Query<PlanNode>(
				"SELECT '' AS Board, Key, Version, Status, Name, Body, CommitRef, Priority, PrevKey, ActiveFrom, ActiveTo, Created, Updated, Type, NodeId FROM plan_nodes").ToList();

		foreach (var r in rows)
			projectDb.Insert(r with { Board = board }); // preserves all temporal columns

		var copied = projectDb.PlanNodes.Count(n => n.Board == board);
		if (copied != rows.Count)
			throw new InvalidOperationException($"row-count mismatch for board '{board}': {rows.Count} legacy vs {copied} copied");

		MarkMigrated(boardFile);
		_log?.LogInformation("Tasks: migrated board '{Board}' ({Rows} rows) into the per-project file", board, rows.Count);
		return true;
	}

	static void MarkMigrated(string boardFile)
	{
		var dest = boardFile + ".migrated";
		if (File.Exists(dest)) File.Delete(dest);
		File.Move(boardFile, dest);
	}
}
