using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Observability;
using PetBox.Tasks.Contract;

namespace PetBox.Tasks.Services.Scheduling;

// The daily alarm's clock (idea recurring-run-scheduler): one pass shortly after start, then one a
// day. The pass itself is IScheduledWakeService — this class only decides WHEN, so the manual MCP
// trigger (`tasks_schedule_run`) and this loop run the same code. Registered only under
// Features:Tasks and through the host's background-service gate (Program.cs). Modeled on
// RetentionService: grace delay, try/catch per pass, never let one failure end the loop.
//
// "Not later than a day after the date" (spec snooze-wakes-without-a-human) is what a daily cadence
// buys; the pass is idempotent, so a restart that runs an extra pass changes nothing.
public sealed partial class ScheduledWakeJob(
	IServiceProvider services,
	ILogger<ScheduledWakeJob> logger,
	TimeProvider? time = null) : BackgroundService
{
	static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
	static readonly TimeSpan Interval = TimeSpan.FromDays(1);
	readonly TimeProvider _time = time ?? TimeProvider.System;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var invokerScope = BackgroundInvokerScope.Begin(logger, nameof(ScheduledWakeJob));

		// Grace period — let DI + migrations settle before the first pass.
		try { await Task.Delay(Grace, _time, stoppingToken); }
		catch (OperationCanceledException) { return; }

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await using var scope = services.CreateAsyncScope();
				await scope.ServiceProvider.GetRequiredService<IScheduledWakeService>().RunAllAsync(stoppingToken);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				LogPassFailed(logger, ex);
			}

			try { await Task.Delay(Interval, _time, stoppingToken); }
			catch (OperationCanceledException) { break; }
		}
	}

	[LoggerMessage(EventId = 353, Level = LogLevel.Warning, Message = "Scheduled wake pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);
}
