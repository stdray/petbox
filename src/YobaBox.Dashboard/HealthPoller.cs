using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Dashboard;

public sealed partial class HealthPoller(
	IServiceProvider services,
	IHttpClientFactory httpClientFactory,
	IOptions<HealthPollerOptions> options,
	ILogger<HealthPoller> logger) : BackgroundService
{
	readonly HealthPollerOptions _options = options.Value;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// Grace period: let DI + migrations settle.
		try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false); }
		catch (OperationCanceledException) { return; }

		var interval = TimeSpan.FromSeconds(Math.Max(5, _options.HealthPollIntervalSeconds));

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await RunPassAsync(stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				LogPassFailed(logger, ex);
			}

			try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { break; }
		}
	}

	async Task RunPassAsync(CancellationToken ct)
	{
		using var scope = services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<YobaBoxDb>();
		var allServices = await db.Services.ToListAsync(ct);
		var now = DateTime.UtcNow;
		var pushCutoff = now.AddSeconds(-Math.Max(30, _options.PushTtlSeconds));

		var http = httpClientFactory.CreateClient("HealthPoller");
		http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds));

		foreach (var service in allServices)
		{
			if (ct.IsCancellationRequested) return;

			var nextHealth = service.HealthModel switch
			{
				HealthModel.Endpoint => await ProbeEndpointAsync(http, service, ct).ConfigureAwait(false),
				HealthModel.Push => service.CheckedAt is { } checkedAt && checkedAt >= pushCutoff
					? service.Health
					: ServiceHealth.Down,
				_ => ServiceHealth.Unknown,
			};

			if (service.HealthModel != HealthModel.Push || nextHealth == ServiceHealth.Down)
			{
				try
				{
					await db.Services
						.Where(s => s.Key == service.Key)
						.Set(s => s.Health, nextHealth)
						.Set(s => s.CheckedAt, (DateTime?)now)
						.UpdateAsync(token: ct);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					LogUpdateFailed(logger, ex, service.Key);
				}
			}
		}
	}

	static async Task<ServiceHealth> ProbeEndpointAsync(HttpClient http, Service service, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(service.Url))
			return ServiceHealth.Unknown;

		var url = service.Url.EndsWith("/health", StringComparison.OrdinalIgnoreCase)
			? service.Url
			: service.Url.TrimEnd('/') + "/health";

		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			var code = (int)resp.StatusCode;
			return code switch
			{
				>= 200 and < 300 => ServiceHealth.Healthy,
				>= 500 => ServiceHealth.Degraded,
				_ => ServiceHealth.Down,
			};
		}
		catch (TaskCanceledException) when (!ct.IsCancellationRequested)
		{
			return ServiceHealth.Down;
		}
		catch (HttpRequestException)
		{
			return ServiceHealth.Down;
		}
	}

	[LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "Health-poll pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);

	[LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Health-poll: failed to update {ServiceKey}")]
	static partial void LogUpdateFailed(ILogger logger, Exception ex, string serviceKey);
}
