using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data;

// Periodically removes per-project task-board `.db` files (and their .wal/.shm sidecars)
// whose owning project no longer exists. A project delete cascades away its TaskBoards
// metadata via ProjectDeletion (a bulk row delete that bypasses TaskBoardStore.DeleteAsync,
// which only tears down a single board's rows and never the shared file), leaving the file
// orphaned. All of a project's boards share one file (tasks/{project}.db), so the file's
// lifecycle == the project's — see ProjectFileOrphans. Mirrors
// PetBox.Data.OrphanCleanupService / PetBox.Log.Core.LogOrphanCleanupService.
public sealed partial class TaskBoardOrphanCleanupService(
	ICoreDbFactory coreDb,
	IScopedDbFactory<TasksDb> factory,
	ILogger<TaskBoardOrphanCleanupService> logger) : BackgroundService
{
	public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// Grace period — let DI + migrations settle.
		try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
		catch (OperationCanceledException) { return; }

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await RunOncePassAsync(stoppingToken);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				LogPassFailed(logger, ex);
			}

			try { await Task.Delay(DefaultInterval, stoppingToken); }
			catch (OperationCanceledException) { break; }
		}
	}

	// Exposed as internal so tests can drive a single pass deterministically.
	internal async Task RunOncePassAsync(CancellationToken ct)
	{
		using var db = coreDb.Open();
		foreach (var projectKey in await ProjectFileOrphans.ReclaimRootFilesAsync(db, factory, ct))
			LogOrphanRemoved(logger, projectKey);
	}

	[LoggerMessage(EventId = 320, Level = LogLevel.Information,
		Message = "Removed orphan task-board file for deleted project: {ProjectKey}")]
	static partial void LogOrphanRemoved(ILogger logger, string projectKey);

	[LoggerMessage(EventId = 321, Level = LogLevel.Warning, Message = "Task-board orphan cleanup pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);
}
