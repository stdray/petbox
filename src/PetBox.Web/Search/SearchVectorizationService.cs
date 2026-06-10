using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PetBox.Web.Search;

// Background drain of Class-B (vector) indexes: the write path enqueues nothing and never blocks on
// embedding; this service periodically pulls each registered IVectorizationJob's delta and
// materializes vectors (spec: write-never-blocks / durable-backfill). One drain pass per interval;
// a failure is logged and retried next tick (the worker holds the cursor so nothing is lost). The
// 30s initial delay also keeps it inert during build-time OpenAPI generation, where the host is
// started through StartAsync and stopped before the first tick (m-d3b39b66).
public sealed partial class SearchVectorizationService : BackgroundService
{
	public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
	static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

	readonly IServiceProvider _services;
	readonly ILogger<SearchVectorizationService> _logger;

	public SearchVectorizationService(IServiceProvider services, ILogger<SearchVectorizationService> logger)
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
		foreach (var job in scope.ServiceProvider.GetServices<IVectorizationJob>())
		{
			if (ct.IsCancellationRequested) return;
			var indexed = await job.DrainAllAsync(ct);
			if (indexed > 0)
				LogVectorized(_logger, indexed, job.GetType().Name);
		}
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "search vectorization pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);

	[LoggerMessage(Level = LogLevel.Debug, Message = "vectorized {Count} docs ({Job})")]
	static partial void LogVectorized(ILogger logger, int count, string job);
}
