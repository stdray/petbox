using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PetBox.Core.Data;

// Periodically snapshots every internal SQLite db (Backup.SnapshotAll). First pass
// runs shortly after startup, then on the interval. A separate pre-migration
// snapshot is taken inline in Program.cs before MigrationRunner runs.
public sealed partial class BackupService(string dataDir, ILogger<BackupService> logger) : BackgroundService
{
	public static readonly TimeSpan Interval = TimeSpan.FromHours(12);
	public const int RetainSets = 14;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
		catch (OperationCanceledException) { return; }

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				var copied = Backup.SnapshotAll(dataDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), "auto", RetainSets);
				LogSnapshot(logger, copied.Count);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex) { LogFailed(logger, ex); }

			try { await Task.Delay(Interval, stoppingToken); }
			catch (OperationCanceledException) { break; }
		}
	}

	[LoggerMessage(EventId = 320, Level = LogLevel.Information, Message = "Backup snapshot wrote {Count} db file(s)")]
	static partial void LogSnapshot(ILogger logger, int count);

	[LoggerMessage(EventId = 321, Level = LogLevel.Warning, Message = "Backup snapshot failed")]
	static partial void LogFailed(ILogger logger, Exception ex);
}
