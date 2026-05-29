using System.Text.Json;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Dashboard;

// Pull side of health: periodically GETs each enabled HealthEndpoint URL,
// expects the status structure { svc, name?, tags{}, version?, sha?, buildDate?,
// status }, and appends a HealthReport (Source="pull"). Push side is
// PetBox.Web.Health.HealthApi (POST /api/health). On fetch/parse failure we skip
// — the status page flags a (svc,tags) whose latest report has gone stale.
public sealed partial class HealthPoller(
	IServiceProvider services,
	IHttpClientFactory httpClientFactory,
	ILogger<HealthPoller> logger) : BackgroundService
{
	static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
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

	sealed record PolledStatus(
		string? Svc, string? Name, Dictionary<string, string>? Tags,
		string? Version, string? Sha, string? BuildDate, string? Status);

	async Task RunPassAsync(DashboardSettings settings, CancellationToken ct)
	{
		using var scope = services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		var endpoints = await db.HealthEndpoints.Where(e => e.Enabled).ToListAsync(ct);
		if (endpoints.Count == 0) return;

		var http = httpClientFactory.CreateClient("HealthPoller");
		http.Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds));
		var now = DateTime.UtcNow;

		foreach (var ep in endpoints)
		{
			if (ct.IsCancellationRequested) return;
			try
			{
				using var resp = await http.GetAsync(ep.Url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
				resp.EnsureSuccessStatusCode();
				var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
				var polled = JsonSerializer.Deserialize<PolledStatus>(json, JsonOpts);
				if (polled is null || string.IsNullOrWhiteSpace(polled.Svc))
				{
					LogBadResponse(logger, ep.Url);
					continue;
				}

				// Endpoint owns the project; ensure the project tag matches it.
				var tags = polled.Tags is null
					? new Dictionary<string, string>(StringComparer.Ordinal)
					: new Dictionary<string, string>(polled.Tags, StringComparer.Ordinal);
				tags["project"] = ep.ProjectKey;

				await db.InsertAsync(new HealthReport
				{
					Svc = polled.Svc.Trim(),
					Name = polled.Name,
					Tags = HealthTags.Canonical(tags),
					Version = polled.Version,
					Sha = polled.Sha,
					BuildDate = polled.BuildDate,
					Status = string.IsNullOrWhiteSpace(polled.Status) ? "unknown" : polled.Status.Trim(),
					ReceivedAt = now,
					Source = "pull",
				}, token: ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				LogPollFailed(logger, ex, ep.Url);
			}
		}
	}

	[LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "Health-poll pass failed")]
	static partial void LogPassFailed(ILogger logger, Exception ex);

	[LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Health-poll: GET failed for {Url}")]
	static partial void LogPollFailed(ILogger logger, Exception ex, string url);

	[LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "Health-poll: malformed/empty status from {Url}")]
	static partial void LogBadResponse(ILogger logger, string url);
}
