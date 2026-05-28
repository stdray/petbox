using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Core.Settings;

namespace YobaBox.Dashboard;

public sealed partial class HealthPoller(
	IServiceProvider services,
	IHttpClientFactory httpClientFactory,
	ILogger<HealthPoller> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// Grace period: let DI + migrations settle.
		try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false); }
		catch (OperationCanceledException) { return; }

		while (!stoppingToken.IsCancellationRequested)
		{
			DashboardSettings settings;
			try
			{
				settings = await ReadSettingsAsync(stoppingToken).ConfigureAwait(false);
				await RunPassAsync(settings, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				LogPassFailed(logger, ex);
				settings = new DashboardSettings();
			}

			try
			{
				var interval = TimeSpan.FromSeconds(Math.Max(5, settings.HealthPollIntervalSeconds));
				await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { break; }
		}
	}

	async Task<DashboardSettings> ReadSettingsAsync(CancellationToken ct)
	{
		using var scope = services.CreateScope();
		var resolver = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
		return await resolver.GetAsync<DashboardSettings>(Scope.System, "$", ct).ConfigureAwait(false);
	}

	async Task RunPassAsync(DashboardSettings settings, CancellationToken ct)
	{
		using var scope = services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<YobaBoxDb>();
		var allServices = await db.Services.ToListAsync(ct);
		var now = DateTime.UtcNow;
		var pushCutoff = now.AddSeconds(-Math.Max(30, settings.PushTtlSeconds));

		var http = httpClientFactory.CreateClient("HealthPoller");
		http.Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds));

		foreach (var service in allServices)
		{
			if (ct.IsCancellationRequested) return;

			// Skip Endpoint-model services that don't have a probeable URL
			// configured. Without a URL we have no signal — better silent than
			// a stream of "failed to update" warnings about a row no-one cares
			// about. Push-model services are independent of URL.
			if (service.HealthModel == HealthModel.Endpoint && !IsProbeableUrl(service.Url))
				continue;

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

	static bool IsProbeableUrl(string? url) =>
		!string.IsNullOrWhiteSpace(url)
		&& Uri.TryCreate(url, UriKind.Absolute, out var uri)
		&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

	[LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "Health-poll pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);

	[LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Health-poll: failed to update {ServiceKey}")]
	static partial void LogUpdateFailed(ILogger logger, Exception ex, string serviceKey);
}
