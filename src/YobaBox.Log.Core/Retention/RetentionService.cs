using LinqToDB;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Log.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace YobaBox.Log.Core.Retention;

public sealed partial class RetentionService(
	IServiceProvider services,
	IOptions<RetentionOptions> options,
	ILogger<RetentionService> logger) : BackgroundService
{
	readonly RetentionOptions _options = options.Value;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
		catch (OperationCanceledException) { return; }

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await RunPassAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				LogPassFailed(logger, ex);
			}

			try { await Task.Delay(_options.RunInterval, stoppingToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { break; }
		}
	}

	public async Task RunPassAsync(DateTime now, CancellationToken ct)
	{
		using var scope = services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<YobaBoxDb>();
		var logFactory = scope.ServiceProvider.GetRequiredService<ILogDbFactory>();

		var projects = await db.Projects.ToListAsync(ct);
		var policies = await db.RetentionPolicies.ToListAsync(ct);
		var policyByProject = policies.ToDictionary(p => p.ProjectKey, StringComparer.Ordinal);

		foreach (var project in projects)
		{
			var retainDays = policyByProject.TryGetValue(project.Key, out var p)
				? p.RetainDays
				: project.Key == "$system"
					? _options.SystemRetainDays
					: _options.DefaultRetainDays;

			var cutoff = now.AddDays(-retainDays);
			var cutoffMs = new DateTimeOffset(cutoff, TimeSpan.Zero).ToUnixTimeMilliseconds();
			try
			{
				var logDb = logFactory.GetLogDb(project.Key);
				var deleted = await logDb.LogEntries
					.Where(e => e.TimestampMs < cutoffMs)
					.DeleteAsync(token: ct);
				if (deleted > 0)
					LogSwept(logger, project.Key, deleted, cutoff);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				LogProjectFailed(logger, ex, project.Key);
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
}
