using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Memory.Data;

// Periodically removes a deleted project's memory-store `.db` files (and their .wal/.shm
// sidecars) plus the now-empty {project} directory. A project delete cascades away its
// MemoryStores metadata via ProjectDeletion (a bulk row delete that bypasses
// MemoryStore.DeleteAsync, which reclaims a single store's file), leaving the files
// orphaned. Reclaimed per-project (project existence is the orphan signal) — see
// ProjectFileOrphans. Mirrors PetBox.Data.OrphanCleanupService /
// PetBox.Log.Core.LogOrphanCleanupService.
public sealed partial class MemoryOrphanCleanupService(
	IServiceProvider services,
	IScopedDbFactory<MemoryDb> factory,
	ILogger<MemoryOrphanCleanupService> logger) : BackgroundService
{
	public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
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
		using var scope = services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		foreach (var projectKey in await ProjectFileOrphans.ReclaimProjectDirsAsync(db, factory, ct))
			LogOrphanRemoved(logger, projectKey);
	}

	[LoggerMessage(EventId = 330, Level = LogLevel.Information,
		Message = "Removed orphan memory-store files for deleted project: {ProjectKey}")]
	static partial void LogOrphanRemoved(ILogger logger, string projectKey);

	[LoggerMessage(EventId = 331, Level = LogLevel.Warning, Message = "Memory orphan cleanup pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);
}
