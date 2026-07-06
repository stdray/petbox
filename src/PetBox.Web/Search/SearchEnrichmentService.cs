using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Observability;

namespace PetBox.Web.Search;

// Background enrichment tick: the write path enqueues nothing and never blocks on enrichment;
// this service periodically pulls each registered IBackgroundIndexJob's delta and materializes
// its background index (spec: write-never-blocks / durable-backfill). Most jobs embed vectors,
// but not all — SessionTermIndexJob only tokenizes a lexical FTS index, SessionDigestJob
// distills text — hence "enrichment", not "vectorization" (the old SearchVectorizationService
// name was as much a misnomer as IVectorizationJob). One drain pass per interval; a failure is
// logged and retried next tick (the worker holds the cursor so nothing is lost). The 30s
// initial delay also keeps it inert during build-time OpenAPI generation, where the host is
// started through StartAsync and stopped before the first tick (m-d3b39b66).
public sealed partial class SearchEnrichmentService : BackgroundService
{
	public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
	static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

	readonly IServiceProvider _services;
	readonly ILogger<SearchEnrichmentService> _logger;

	public SearchEnrichmentService(IServiceProvider services, ILogger<SearchEnrichmentService> logger)
	{
		_services = services;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try { await Task.Delay(InitialDelay, stoppingToken); }
		catch (OperationCanceledException) { return; }

		while (!stoppingToken.IsCancellationRequested)
		{
			try { await RunOncePassAsync(stoppingToken); }
			catch (OperationCanceledException) { break; }
			catch (Exception ex) { LogPassFailed(_logger, ex); }

			try { await Task.Delay(Interval, stoppingToken); }
			catch (OperationCanceledException) { break; }
		}
	}

	internal async Task RunOncePassAsync(CancellationToken ct)
	{
		using var scope = _services.CreateScope();
		foreach (var job in scope.ServiceProvider.GetServices<IBackgroundIndexJob>())
		{
			if (ct.IsCancellationRequested) return;
			// One span per job drain (a root trace — there is no inbound request); embed HTTP
			// calls attach as client children. An idle pass is unrecorded — a span per empty
			// tick would flood the trace store (spec: trace-operation-granularity). Span op name
			// and log templates are kept VERBATIM across the rename (observable — dashboards/log
			// queries key off them): this is a pure identifier rename, zero behavior change.
			using var span = PetBoxActivitySources.Search.StartActivity("search.vectorize");
			span?.SetTag("petbox.job", job.GetType().Name);
			var indexed = await job.DrainAllAsync(ct);
			span?.SetTag("petbox.indexed", indexed);
			if (indexed == 0 && span is not null)
			{
				span.IsAllDataRequested = false;
				span.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
			}
			if (indexed > 0)
				LogVectorized(_logger, indexed, job.GetType().Name);
		}
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "search vectorization pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);

	[LoggerMessage(Level = LogLevel.Debug, Message = "vectorized {Count} docs ({Job})")]
	static partial void LogVectorized(ILogger logger, int count, string job);
}
