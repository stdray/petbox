using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Log.Core.Data;

namespace PetBox.Log.Core.Retention;

public sealed partial class RetentionService(
	IServiceProvider services,
	ILogger<RetentionService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// Grace period — let DI + migrations settle.
		try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
		catch (OperationCanceledException) { return; }

		while (!stoppingToken.IsCancellationRequested)
		{
			TimeSpan nextDelay;
			try
			{
				nextDelay = await RunPassAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				LogPassFailed(logger, ex);
				nextDelay = TimeSpan.FromHours(1);
			}

			try { await Task.Delay(nextDelay, stoppingToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { break; }
		}
	}

	public async Task<TimeSpan> RunPassAsync(DateTime now, CancellationToken ct)
	{
		using var scope = services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		var resolver = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();

		var systemDefaults = await resolver.GetAsync<LogSettings>(Scope.System, "$", ct).ConfigureAwait(false);
		var nextDelay = TimeSpan.FromSeconds(Math.Max(60, systemDefaults.RunIntervalSeconds));

		// Retention runs per named log. Settings cascade at project scope; the
		// self-log (in the system project) gets the system retention window.
		var logs = await db.Logs.ToListAsync(ct);

		foreach (var log in logs)
		{
			var logRef = $"{log.ProjectKey}/{log.Name}";
			var settings = await resolver.GetAsync<LogSettings>(Scope.Project, log.ProjectKey, ct).ConfigureAwait(false);
			var retainDays = log.ProjectKey == LogNames.SystemProject ? settings.SystemRetainDays : settings.RetentionDays;

			var cutoff = now.AddDays(-retainDays);
			var cutoffMs = new DateTimeOffset(cutoff, TimeSpan.Zero).ToUnixTimeMilliseconds();
			try
			{
				using var logDb = store.GetContext(log.ProjectKey, log.Name);
				var deleted = await logDb.LogEntries
					.Where(e => e.TimestampMs < cutoffMs)
					.DeleteAsync(token: ct);
				if (deleted > 0)
					LogSwept(logger, logRef, deleted, cutoff);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				LogProjectFailed(logger, ex, logRef);
			}
		}

		try
		{
			var expired = await db.ShareLinks.Where((ShareLink s) => s.ExpiresAt < now).DeleteAsync(token: ct);
			if (expired > 0)
				LogShareLinksSwept(logger, expired);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			LogShareLinksFailed(logger, ex);
		}

		try
		{
			var dash = await resolver.GetAsync<DashboardSettings>(Scope.System, "$", ct).ConfigureAwait(false);
			var healthCutoff = now.AddDays(-Math.Max(1, dash.HealthRetentionDays));
			var deletedHealth = await db.HealthReports.Where(h => h.ReceivedAt < healthCutoff).DeleteAsync(token: ct);
			if (deletedHealth > 0)
				LogHealthSwept(logger, deletedHealth);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			LogHealthFailed(logger, ex);
		}

		return nextDelay;
	}

	[LoggerMessage(EventId = 100, Level = LogLevel.Information,
		Message = "Retention swept {Project}: deleted {Count} events older than {Cutoff:O}")]
	static partial void LogSwept(ILogger logger, string project, int count, DateTime cutoff);

	[LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Retention failed on project {Project}")]
	static partial void LogProjectFailed(ILogger logger, Exception ex, string project);

	[LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "Retention pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);

	[LoggerMessage(EventId = 103, Level = LogLevel.Information,
		Message = "Retention: deleted {Count} expired share links")]
	static partial void LogShareLinksSwept(ILogger logger, int count);

	[LoggerMessage(EventId = 104, Level = LogLevel.Error, Message = "Share-link retention sweep failed")]
	static partial void LogShareLinksFailed(ILogger logger, Exception ex);

	[LoggerMessage(EventId = 105, Level = LogLevel.Information,
		Message = "Retention: deleted {Count} expired health reports")]
	static partial void LogHealthSwept(ILogger logger, int count);

	[LoggerMessage(EventId = 106, Level = LogLevel.Error, Message = "Health-report retention sweep failed")]
	static partial void LogHealthFailed(ILogger logger, Exception ex);
}
