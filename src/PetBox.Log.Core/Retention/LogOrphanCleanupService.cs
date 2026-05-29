using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Log.Core.Data;

namespace PetBox.Log.Core.Retention;

// Periodically removes log `.db` files (and their .wal/.shm sidecars) that no
// longer have a corresponding row in the Logs metadata table. DELETE on the log
// lifecycle removes the row + tries to delete the file; if a write is in-flight
// on Windows the file stays locked and this service mops up on its next tick.
// Mirrors PetBox.Data.OrphanCleanupService for the user-data module.
public sealed partial class LogOrphanCleanupService(
	IServiceProvider services,
	IScopedDbFactory<LogDb> factory,
	ILogger<LogOrphanCleanupService> logger) : BackgroundService
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

	internal async Task RunOncePassAsync(CancellationToken ct)
	{
		using var scope = services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();

		var knownProjects = await db.Logs
			.Select(l => l.ProjectKey)
			.Distinct()
			.ToListAsync(ct);
		var diskProjects = ScopedDbFiles.ListScopeKeys(factory.BaseDir);
		var projects = knownProjects.Union(diskProjects, StringComparer.Ordinal).ToList();

		foreach (var projectKey in projects)
		{
			if (ct.IsCancellationRequested) return;

			var knownNames = await db.Logs
				.Where(l => l.ProjectKey == projectKey)
				.Select(l => l.Name)
				.ToListAsync(ct);
			var knownSet = new HashSet<string>(knownNames, StringComparer.Ordinal);

			foreach (var diskName in ScopedDbFiles.ListNames(factory.BaseDir, projectKey))
			{
				if (knownSet.Contains(diskName)) continue;
				await factory.EvictAsync(projectKey, diskName);
				if (ScopedDbFiles.TryDelete(ScopedDbFiles.PathFor(factory.BaseDir, projectKey, diskName)))
					LogOrphanRemoved(logger, projectKey, diskName);
			}
		}
	}

	[LoggerMessage(EventId = 310, Level = LogLevel.Information,
		Message = "Removed orphan log file: {ProjectKey}/{LogName}")]
	static partial void LogOrphanRemoved(ILogger logger, string projectKey, string logName);

	[LoggerMessage(EventId = 311, Level = LogLevel.Warning, Message = "Log orphan cleanup pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);
}
