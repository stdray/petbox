using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Sessions.Data;

// Periodically removes a deleted project's sessions `.db` file (and its .wal/.shm sidecars)
// once the owning project no longer exists. Sessions are a single per-project file
// (sessions/{project}.db) with no catalog, so the file's lifecycle == the project's; a
// project delete removes the Project row and this mops up the file — see ProjectFileOrphans.
// Mirrors PetBox.Data.OrphanCleanupService / PetBox.Log.Core.LogOrphanCleanupService.
public sealed partial class SessionOrphanCleanupService(
	IServiceProvider services,
	IScopedDbFactory<SessionsDb> factory,
	ILogger<SessionOrphanCleanupService> logger) : BackgroundService
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
		foreach (var projectKey in await ProjectFileOrphans.ReclaimRootFilesAsync(db, factory, ct))
			LogOrphanRemoved(logger, projectKey);
	}

	[LoggerMessage(EventId = 340, Level = LogLevel.Information,
		Message = "Removed orphan sessions file for deleted project: {ProjectKey}")]
	static partial void LogOrphanRemoved(ILogger logger, string projectKey);

	[LoggerMessage(EventId = 341, Level = LogLevel.Warning, Message = "Session orphan cleanup pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);
}
