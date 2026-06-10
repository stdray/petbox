using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Deploy.Contract;

namespace PetBox.Deploy.Services;

// Background failover: on a fixed tick, relocate relocatable deployments off nodes whose
// last contact is older than Staleness onto an online node that covers their RequiredTags.
// Staleness > the agent poll interval with hysteresis (~3 missed polls) so a single blip
// doesn't trigger a move. The relocation decision + db write live in DeployService; this is
// just the timer + scope.
public sealed partial class DeployFailoverSweeper(IServiceScopeFactory scopes, ILogger<DeployFailoverSweeper> logger) : BackgroundService
{
	public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);
	// ~3 missed 30s polls. A node must be silent this long before its work is moved.
	public static readonly TimeSpan Staleness = TimeSpan.FromSeconds(90);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using var scope = scopes.CreateScope();
				var svc = scope.ServiceProvider.GetRequiredService<IDeployService>();
				foreach (var a in await svc.RescheduleStaleAsync(Staleness, stoppingToken))
				{
					if (a.Relocated) LogRelocated(logger, a.Service, a.FromNode, a.ToNode!);
					else LogNoTarget(logger, a.Service, a.FromNode, a.Reason);
				}
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex) { LogFailed(logger, ex); }

			try { await Task.Delay(Period, stoppingToken); }
			catch (OperationCanceledException) { break; }
		}
	}

	[LoggerMessage(EventId = 700, Level = LogLevel.Warning, Message = "Failover: relocated {Service} from stale node {FromNode} to {ToNode}")]
	static partial void LogRelocated(ILogger logger, string service, string fromNode, string toNode);

	[LoggerMessage(EventId = 701, Level = LogLevel.Warning, Message = "Failover: {Service} on stale node {FromNode} not relocated — {Reason}")]
	static partial void LogNoTarget(ILogger logger, string service, string fromNode, string reason);

	[LoggerMessage(EventId = 702, Level = LogLevel.Error, Message = "Failover sweep failed")]
	static partial void LogFailed(ILogger logger, Exception ex);
}
