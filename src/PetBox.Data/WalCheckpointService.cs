using LinqToDB;
using LinqToDB.Async;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Data;

// Periodically runs `PRAGMA wal_checkpoint(TRUNCATE)` against every active
// DataDb. Without this, the `.wal` sidecar grows unbounded on hot writers
// when the SQLite connection lifecycle is per-HTTP-request — there's no
// long-lived connection to perform the auto-checkpoint that vanilla SQLite
// relies on.
//
// The TRUNCATE variant moves all pages from `.wal` back into the main DB file
// AND truncates the WAL to zero, freeing disk space. Safe under concurrent
// readers — if a checkpoint can't complete because a reader is in mid-read,
// SQLite returns SQLITE_BUSY and we just retry next tick.
public sealed partial class WalCheckpointService(
	IServiceProvider services,
	IDataDbFactory factory,
	ILogger<WalCheckpointService> logger) : BackgroundService
{
	public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
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

		var dbs = await db.DataDbs
			.Select(d => new { d.ProjectKey, d.Name })
			.ToListAsync(ct);

		foreach (var entry in dbs)
		{
			if (ct.IsCancellationRequested) return;

			try
			{
				// The ONE write path that deliberately runs WITHOUT the disk quota, and it must
				// stay that way. Every user write goes through IDataDbFactory.OpenAsync, which
				// re-applies PRAGMA max_page_count (it is per-connection, not stored in the file).
				// This is maintenance: it only folds WAL pages that a QUOTA'D writer was already
				// allowed to write into the main file, so it cannot smuggle a pet past its quota.
				// Quota'ing it would be actively harmful: if a quota is ever lowered below a db's
				// current size, the checkpoint would fail with SQLITE_FULL forever and the -wal
				// sidecar would grow without bound — filling the disk by way of the very rule
				// meant to prevent it. Do not "fix" this to use OpenAsync.
				var cs = factory.GetConnectionString(entry.ProjectKey, entry.Name);
				await using var conn = new SqliteConnection(cs);
				await conn.OpenAsync(ct);
				await using var cmd = conn.CreateCommand();
				cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
				// PRAGMA wal_checkpoint returns (busy, log, checkpointed); we
				// ignore the values — SQLITE_BUSY just means retry later, and
				// the others are informational.
				await cmd.ExecuteScalarAsync(ct);
			}
			catch (FileNotFoundException)
			{
				// DataDb row exists but file gone (e.g. mid-orphan-cleanup); ignore.
			}
			catch (SqliteException ex)
			{
				LogDbSkipped(logger, ex, entry.ProjectKey, entry.Name);
			}
		}
	}

	[LoggerMessage(EventId = 310, Level = LogLevel.Warning,
		Message = "WAL checkpoint skipped for {ProjectKey}/{DbName}")]
	static partial void LogDbSkipped(ILogger logger, Exception ex, string projectKey, string dbName);

	[LoggerMessage(EventId = 311, Level = LogLevel.Warning, Message = "WAL checkpoint pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);
}
