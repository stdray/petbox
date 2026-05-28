using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YobaBox.Core.Data;

namespace YobaBox.Data;

// Periodically removes DataDb files (and their .wal/.shm sidecars) that no
// longer have a corresponding row in DataDbs. DELETE on the lifecycle endpoint
// removes the row immediately and tries to delete the file; if a query is
// in-flight on Windows the file remains locked and the delete returns false.
// This service mops up on its next tick.
//
// We deliberately avoid refcount/coordination between the request pipeline
// and DELETE — simpler. Eventually-consistent file cleanup is enough for a
// single-pet topology and survives across process restarts (next tick the
// service walks the disk and reconciles against DataDbs).
public sealed partial class OrphanCleanupService(
	IServiceProvider services,
	IDataDbFactory factory,
	ILogger<OrphanCleanupService> logger) : BackgroundService
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

	// Exposed as internal so tests can drive a single pass deterministically
	// without waiting on the 1-minute interval.
	internal async Task RunOncePassAsync(CancellationToken ct)
	{
		using var scope = services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<YobaBoxDb>();

		// Every project that has either metadata rows OR on-disk files.
		var knownProjects = await db.DataDbs
			.Select(d => d.ProjectKey)
			.Distinct()
			.ToListAsync(ct);
		var diskProjects = factory.ListProjectDirectories();
		var projects = knownProjects.Union(diskProjects, StringComparer.Ordinal).ToList();

		foreach (var projectKey in projects)
		{
			if (ct.IsCancellationRequested) return;

			var knownNames = await db.DataDbs
				.Where(d => d.ProjectKey == projectKey)
				.Select(d => d.Name)
				.ToListAsync(ct);
			var knownSet = new HashSet<string>(knownNames, StringComparer.Ordinal);

			foreach (var diskName in factory.ListPhysicalDbs(projectKey))
			{
				if (knownSet.Contains(diskName)) continue;
				if (factory.TryDelete(projectKey, diskName))
					LogOrphanRemoved(logger, projectKey, diskName);
			}
		}
	}

	[LoggerMessage(EventId = 300, Level = LogLevel.Information,
		Message = "Removed orphan DataDb file: {ProjectKey}/{DbName}")]
	static partial void LogOrphanRemoved(ILogger logger, string projectKey, string dbName);

	[LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "Orphan cleanup pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);
}
