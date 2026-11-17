using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Observability;
using PetBox.Core.Settings;
using PetBox.Log.Core.Data;

namespace PetBox.Log.Core.Retention;

public sealed partial class RetentionService(
	IServiceProvider services,
	ICoreDbFactory coreDb,
	ILogger<RetentionService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// chore background-invoker-not-tagged-in-logs: covers the whole hosted lifetime,
		// including LogPassFailed below (outside RunPassAsync's own narrower scope).
		using var invokerScope = BackgroundInvokerScope.Begin(logger, nameof(RetentionService));

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
		// chore background-invoker-not-tagged-in-logs: every self-log record this pass emits
		// (LogSwept/LogProjectFailed/etc.) carries Invoker="background:RetentionService" —
		// stamped here, at the one place a pass begins, so a test calling RunPassAsync directly
		// (RetentionServiceTests) observes the exact same attribution as the hosted loop.
		using var invokerScope = BackgroundInvokerScope.Begin(logger, nameof(RetentionService));
		using var scope = services.CreateScope();
		using var db = coreDb.Open();
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

			// spec log-retention-cascade: the log's OWN window wins if it has one; otherwise fall
			// back to the project → workspace → system cascade exactly as before this column
			// existed (log.RetentionDays is NULL for every pre-existing log — a true no-op).
			int retainDays;
			if (log.RetentionDays is { } ownWindow)
			{
				retainDays = ownWindow;
			}
			else
			{
				var settings = await resolver.GetAsync<LogSettings>(Scope.Project, log.ProjectKey, ct).ConfigureAwait(false);
				retainDays = log.ProjectKey == LogNames.SystemProject ? settings.SystemRetainDays : settings.RetentionDays;
			}

			var cutoff = now.AddDays(-retainDays);
			var cutoffMs = new DateTimeOffset(cutoff, TimeSpan.Zero).ToUnixTimeMilliseconds();
			try
			{
				using var logDb = store.NewEnsuredContext(log.ProjectKey, log.Name);

				// Every sweep here is BATCHED, and that is not an optimisation — it is the difference
				// between a background pass and an outage. A single DELETE over a table that grew without
				// a window holds SQLite's write lock for the whole scan: the first pass after this sweep
				// shipped locked $system/petbox for minutes, `/v1/traces` died on its 30 s busy timeout and
				// the log wrote nothing at all (2026-11-17, caught in the post-deploy smoke).
				var deleted = await SweepInBatchesAsync(
					logDb.LogEntries.Where(e => e.TimestampMs < cutoffMs),
					e => e.Id,
					ids => logDb.LogEntries.Where(e => ids.Contains(e.Id)),
					ct);
				if (deleted > 0)
					LogSwept(logger, logRef, deleted, cutoff);

				// The window belongs to the LOG, not to one table: a log that keeps 7 days keeps 7 days
				// of every signal it stores. Sweeping LogEntries alone left Spans and MetricPoints
				// growing without any bound, in the same .db, the same -wal/-shm sidecars and the same
				// backups (observation retention-does-not-sweep-spans-and-metric-points).
				//
				// `> 0` is deliberate, and it is the reason this is not one shared predicate with the
				// events sweep: TimestampMs is stamped on ingest, but these two carry the producer's
				// raw OTLP nanos, and a client that omits the field lands at 0. Such a row is
				// UNSTAMPED, not ancient — a bare `< cutoff` would delete it on the very first pass
				// and look like healthy retention while silently dropping data.
				var cutoffNs = cutoffMs * 1_000_000L;
				var deletedSpans = await SweepInBatchesAsync(
					logDb.Spans.Where(s => s.StartUnixNs > 0 && s.StartUnixNs < cutoffNs),
					s => s.SpanId,
					ids => logDb.Spans.Where(s => ids.Contains(s.SpanId)),
					ct);
				var deletedPoints = await SweepInBatchesAsync(
					logDb.MetricPoints.Where(m => m.TimeUnixNs > 0 && m.TimeUnixNs < cutoffNs),
					m => m.Id,
					ids => logDb.MetricPoints.Where(m => ids.Contains(m.Id)),
					ct);
				if (deletedSpans > 0 || deletedPoints > 0)
					LogSignalsSwept(logger, logRef, deletedSpans, deletedPoints, cutoff);
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

		// The OTHER share-token family (M053, spec node-share). Same sweep, ONE deliberate difference:
		// `ExpiresAt != null` is written out even though SQLite would never match a NULL against `<`
		// anyway. A never-expiring link (spec `node-share-lifetime`) is not "expired long ago", it has
		// no expiry — and that has to be legible HERE, in the job that would otherwise be the thing
		// silently deleting it, rather than left to a reader's knowledge of three-valued logic.
		try
		{
			var expiredShares = await db.NodeShares
				.Where((NodeShare s) => s.ExpiresAt != null && s.ExpiresAt < now)
				.DeleteAsync(token: ct);
			if (expiredShares > 0)
				LogNodeSharesSwept(logger, expiredShares);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			LogNodeSharesFailed(logger, ex);
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

	// Bounded batches, one transaction each: read the next N stale keys, delete exactly those, repeat.
	// 500 keeps the IN-list under SQLite's parameter limit (the same reason InsertOrIgnore chunks at
	// 900) and keeps every transaction short enough that the log's writers interleave instead of
	// queueing behind a million-row scan. Safe against concurrent writes: staleness is by timestamp,
	// so a row written while this pass runs is newer than the cutoff and never lands in a batch.
	const int SweepBatchSize = 500;

	static async Task<int> SweepInBatchesAsync<TRow, TKey>(
		IQueryable<TRow> stale,
		Func<TRow, TKey> keyOf,
		Func<IReadOnlyList<TKey>, IQueryable<TRow>> byKeys,
		CancellationToken ct)
		where TKey : notnull
	{
		var total = 0;
		while (true)
		{
			var batch = await stale.Take(SweepBatchSize).ToListAsync(ct).ConfigureAwait(false);
			if (batch.Count == 0)
				return total;
			total += await byKeys(batch.Select(keyOf).ToList()).DeleteAsync(token: ct).ConfigureAwait(false);
		}
	}

	[LoggerMessage(EventId = 100, Level = LogLevel.Information,
		Message = "Retention swept {Project}: deleted {Count} events older than {Cutoff:O}")]
	static partial void LogSwept(ILogger logger, string project, int count, DateTime cutoff); [LoggerMessage(EventId = 109, Level = LogLevel.Information,
		Message = "Retention swept {Project}: deleted {Spans} spans and {MetricPoints} metric points older than {Cutoff:O}")]
	static partial void LogSignalsSwept(ILogger logger, string project, int spans, int metricPoints, DateTime cutoff);

	[LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Retention failed on project {Project}")]
	static partial void LogProjectFailed(ILogger logger, Exception ex, string project);

	[LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "Retention pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);

	[LoggerMessage(EventId = 103, Level = LogLevel.Information,
		Message = "Retention: deleted {Count} expired share links")]
	static partial void LogShareLinksSwept(ILogger logger, int count);

	[LoggerMessage(EventId = 104, Level = LogLevel.Error, Message = "Share-link retention sweep failed")]
	static partial void LogShareLinksFailed(ILogger logger, Exception ex);

	[LoggerMessage(EventId = 107, Level = LogLevel.Information,
		Message = "Retention: deleted {Count} expired node share links")]
	static partial void LogNodeSharesSwept(ILogger logger, int count);

	[LoggerMessage(EventId = 108, Level = LogLevel.Error, Message = "Node-share retention sweep failed")]
	static partial void LogNodeSharesFailed(ILogger logger, Exception ex);

	[LoggerMessage(EventId = 105, Level = LogLevel.Information,
		Message = "Retention: deleted {Count} expired health reports")]
	static partial void LogHealthSwept(ILogger logger, int count);

	[LoggerMessage(EventId = 106, Level = LogLevel.Error, Message = "Health-report retention sweep failed")]
	static partial void LogHealthFailed(ILogger logger, Exception ex);
}
