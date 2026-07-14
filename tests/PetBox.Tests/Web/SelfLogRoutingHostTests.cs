using System.Net;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Tests.Web;

// spec self-telemetry-log-routing, end-to-end through the real host: an access-line event
// (RequestLoggingMiddleware, EventId 500-503) must land in `access` and NOT `petbox`; an ordinary
// self-log event must land in `petbox` and NOT `access`. Complements SelfLogRouterTests (pure
// routing/split logic) and SystemLogFlusherTests (the flusher's split, without the HTTP host) with
// proof that the boot-time wiring (Program.cs) and the middleware (RequestLoggingMiddleware) hold
// up end to end.
public sealed class SelfLogRoutingHostTests : IAsyncLifetime
{
	const string ApiKey = "yb_key_system_internal"; // seeded by M004, scopes include logs:query.

	WebApplicationFactory<Program> _factory = null!;
	HttpClient _client = null!;

	public async Task InitializeAsync()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		// SystemLoggerProvider/SystemLogFlusher are gated at BUILD time (Program.cs, before
		// builder.Build()) on Seq:SelfLog:Enabled — WebApplicationFactory's in-memory
		// ConfigureAppConfiguration override is not yet visible at that read point (see
		// McpToolCallMetricsFixture for the same workaround), so it must arrive as an env var.
		Environment.SetEnvironmentVariable("Seq__SelfLog__Enabled", "true");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString("selflog-route"),
						["Features:Logging"] = "true",
						["Seq:SelfLog:Enabled"] = "true",
					});
				});
			});

		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_client = _factory.CreateClient();
		await Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	async Task<int> CountAsync(string logName, string kql)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, $"/api/logs/$system/{logName}/query?q={Uri.EscapeDataString(kql)}");
		req.Headers.Add("X-Api-Key", ApiKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		return doc.RootElement.GetProperty("count").GetInt32();
	}

	async Task WaitForMessageAsync(string logName, string marker)
	{
		var kql = $"events | where Message contains \"{marker}\" | take 1";
		for (var i = 0; i < 400; i++)
		{
			if (await CountAsync(logName, kql) > 0) return;
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException($"marker '{marker}' not visible in $system/{logName} after 10s");
	}

	[Fact]
	public async Task AccessLineEvent_LandsInAccess_NotInPetbox()
	{
		// Any request through RequestLoggingMiddleware produces an EventId 500-503 line
		// regardless of status; an unmatched /api/* path (404, EventId 501/Warning) needs no
		// auth and still carries a marker in the Path itself for a precise KQL search.
		var marker = Guid.NewGuid().ToString("N");
		using (var resp = await _client.GetAsync($"/api/__selftest_access__/{marker}"))
			resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

		await WaitForMessageAsync(LogNames.AccessLog, marker);

		var petboxCount = await CountAsync(LogNames.SelfLog, $"events | where Message contains \"{marker}\" | take 1");
		petboxCount.Should().Be(0, "the access-routed event must not ALSO appear in the default self-log");
	}

	[Fact]
	public async Task OrdinaryEvent_LandsInPetbox_NotInAccess()
	{
		var marker = Guid.NewGuid().ToString("N");
		using (var scope = _factory.Services.CreateScope())
		{
			var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			// A category under the "PetBox" prefix SystemLogger captures, but NOT under the
			// "PetBox.Web.Logging" access rule — matches no route, falls back to `petbox`.
			var logger = loggerFactory.CreateLogger("PetBox.Test.OrdinaryEvent");
			logger.LogInformation("ordinary self-log event {Marker}", marker);
		}

		await WaitForMessageAsync(LogNames.SelfLog, marker);

		var accessCount = await CountAsync(LogNames.AccessLog, $"events | where Message contains \"{marker}\" | take 1");
		accessCount.Should().Be(0, "an ordinary (non-access) event must not land in the access log");
	}
}

// Boot-time creation of the `access` log (self-telemetry-log-routing): created once at startup
// next to `petbox`, with a 14-day retention window, WITHOUT relying on ingest auto-creating it
// (log-create-before-ingest forbids that). A "restart" — a second host pointed at the SAME core.db
// file — must neither fail nor clobber an operator-adjusted retention window back to the 14-day
// default.
public sealed class SelfLogBootTests
{
	static WebApplicationFactory<Program> MakeFactory(string connectionString) =>
		new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = connectionString,
						["Features:Logging"] = "true",
					});
				});
			});

	[Fact]
	public async Task Boot_CreatesAccessLog_With14DayRetention()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		var cs = TestSchema.NewTempConnectionString("selflog-boot-create");

		await using var factory = MakeFactory(cs);
		using var scope = factory.Services.CreateScope();
		var core = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>();
		using var db = core.Open();

		var meta = await db.Logs.FirstOrDefaultAsync(l => l.ProjectKey == LogNames.SystemProject && l.Name == LogNames.AccessLog);
		meta.Should().NotBeNull("the access log must be created at boot, not left to auto-vivify on first ingest");
		meta!.RetentionDays.Should().Be(14);

		// petbox itself must still exist too — the access log is created NEXT TO it, not instead of it.
		var petboxMeta = await db.Logs.FirstOrDefaultAsync(l => l.ProjectKey == LogNames.SystemProject && l.Name == LogNames.SelfLog);
		petboxMeta.Should().NotBeNull();
	}

	[Fact]
	public async Task Restart_DoesNotFail_AndDoesNotResetAnOperatorAdjustedWindow()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		var cs = TestSchema.NewTempConnectionString("selflog-boot-restart");

		await using (var factory1 = MakeFactory(cs))
		{
			using var scope = factory1.Services.CreateScope();
			var core = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>();
			using var db = core.Open();
			var meta = await db.Logs.FirstOrDefaultAsync(l => l.ProjectKey == LogNames.SystemProject && l.Name == LogNames.AccessLog);
			meta.Should().NotBeNull();
			meta!.RetentionDays.Should().Be(14);

			// An operator dials the window up to 30 days — this must survive a restart untouched.
			var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
			var updated = await store.UpdateRetentionDaysAsync(LogNames.SystemProject, LogNames.AccessLog, 30);
			updated.Should().NotBeNull();
			updated!.RetentionDays.Should().Be(30);
		}

		// "Restart": a second host over the SAME core.db file. If the boot path called CreateAsync
		// on an already-existing log, LogStore.CreateAsync throws InvalidOperationException and the
		// host would fail to start — merely reaching the assertions below proves it did not.
		await using var factory2 = MakeFactory(cs);
		using (var scope2 = factory2.Services.CreateScope())
		{
			var core2 = scope2.ServiceProvider.GetRequiredService<ICoreDbFactory>();
			using var db2 = core2.Open();
			var metaAfterRestart = await db2.Logs.FirstOrDefaultAsync(l => l.ProjectKey == LogNames.SystemProject && l.Name == LogNames.AccessLog);
			metaAfterRestart.Should().NotBeNull();
			metaAfterRestart!.RetentionDays.Should().Be(30,
				"an operator's retention override must survive a restart, not be reset to the 14-day default");
		}
	}
}
