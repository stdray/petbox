using System.Net;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Retention;

namespace PetBox.Tests.Web;

// chore background-invoker-not-tagged-in-logs, end to end through the real host: a self-log
// record produced by an actual BackgroundService (RetentionService — RunPassAsync, same
// production entry point RetentionServiceTests drives) must carry Invoker="background:
// RetentionService" once it lands in the queryable `petbox` self-log, and an ordinary event
// logged with no BackgroundInvokerScope active must NOT carry that property. Complements
// BackgroundInvokerScopeTests (the pure SystemLogger write-boundary mechanism, no host) and
// mirrors SelfLogRoutingHostTests' host/query plumbing (Seq:SelfLog:Enabled, the KQL query
// endpoint, WaitForMessageAsync).
public sealed class BackgroundInvokerHostFixture : IAsyncLifetime
{
	public const string TestProjectKey = "invoker-proj";
	public const string ApiKey = "yb_key_system_internal"; // seeded by M004, scopes include logs:query.

	public WebApplicationFactory<Program> Factory { get; }

	public BackgroundInvokerHostFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				// SystemLoggerProvider/SystemLogFlusher are gated at BUILD time (see
				// SelfLogRoutingHostTests) on Seq:SelfLog:Enabled — must be visible pre-Build.
				b.UseSetting("Seq:SelfLog:Enabled", "true");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString("invoker-attr"),
						["Features:Logging"] = "true",
						["Seq:SelfLog:Enabled"] = "true",
						// Deliberately NOT setting Host:BackgroundServices=false here (unlike
						// RetentionServiceFixture): SystemLogFlusher must keep running so records
						// SystemLogger enqueues actually get drained into the queryable self-log.
						// The real hosted RetentionService instance also starts, but its own
						// 30s grace delay means it never interferes with this test.
					});
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new Workspace { Key = "ws-invoker", Name = "WS", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "ws-invoker", Name = "Invoker" });
	}

	public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class BackgroundInvokerHostTests : IClassFixture<BackgroundInvokerHostFixture>
{
	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	public BackgroundInvokerHostTests(BackgroundInvokerHostFixture fx)
	{
		_factory = fx.Factory;
		_client = _factory.CreateClient();
	}

	// Fetches matching rows (not just a count) so the test can inspect each row's own Properties
	// bag directly — sidesteps needing to know which KQL scalar functions the sqlite-backed
	// evaluator supports beyond the indexer syntax KqlPropertyKeysTests already proves works.
	async Task<List<Dictionary<string, string>>> QueryPropertiesAsync(string kql)
	{
		var req = new HttpRequestMessage(HttpMethod.Get,
			$"/api/logs/$system/{LogNames.SelfLog}/query?q={Uri.EscapeDataString(kql)}");
		req.Headers.Add("X-Api-Key", BackgroundInvokerHostFixture.ApiKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		var result = new List<Dictionary<string, string>>();
		foreach (var ev in doc.RootElement.GetProperty("events").EnumerateArray())
		{
			var props = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var p in ev.GetProperty("Properties").EnumerateObject())
			{
				// LogApi re-serializes each property value with JsonSerializer (LogApi.cs ~349),
				// so a JSON string value round-trips as a QUOTED string here (`"background:X"`,
				// not `background:X`) — unwrap it back to the plain value. Non-string properties
				// (e.g. numeric EventId) stay as their raw JSON text; only string-valued keys
				// (Invoker, SourceContext, ...) are ever compared against in this test.
				var raw = p.Value.GetString() ?? "";
				try { props[p.Name] = JsonSerializer.Deserialize<string>(raw) ?? raw; }
				catch (JsonException) { props[p.Name] = raw; }
			}
			result.Add(props);
		}
		return result;
	}

	async Task<List<Dictionary<string, string>>> WaitForRowsAsync(string kql, int timeoutMs = 10_000)
	{
		var elapsed = 0;
		const int step = 25;
		while (elapsed < timeoutMs)
		{
			var rows = await QueryPropertiesAsync(kql);
			if (rows.Count > 0) return rows;
			await Task.Delay(step);
			elapsed += step;
		}
		throw new Xunit.Sdk.XunitException($"query '{kql}' found no match in $system/{LogNames.SelfLog} after {timeoutMs}ms");
	}

	[Fact]
	public async Task RealRetentionServicePass_EmitsSelfLogRecord_TaggedInvokerBackgroundRetentionService()
	{
		// RunPassAsync is the exact method RetentionService.ExecuteAsync calls on every tick —
		// same production code path, driven manually for determinism (RetentionServiceTests
		// pattern), through a FRESH instance so its own BackgroundInvokerScope.Begin call fires.
		// Log names must match ^[a-z][a-z0-9_-]{0,99}$ — start with a letter.
		var marker = "inv-" + Guid.NewGuid().ToString("N");
		using (var scope = _factory.Services.CreateScope())
		{
			var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
			await store.CreateAsync(BackgroundInvokerHostFixture.TestProjectKey, marker, null);

			// LogSwept only fires when deleted > 0 — force a delete via one very old row.
			using (var db = store.NewEnsuredContext(BackgroundInvokerHostFixture.TestProjectKey, marker))
			{
				await db.InsertAsync(new LogEntryRecord
				{
					ServiceKey = "svc",
					TimestampMs = new DateTimeOffset(DateTime.UtcNow.AddYears(-2), TimeSpan.Zero).ToUnixTimeMilliseconds(),
					Level = 0,
					Message = "old",
					MessageTemplate = "old",
					PropertiesJson = "{}",
				});
			}

			var service = ActivatorUtilities.CreateInstance<RetentionService>(_factory.Services);
			await service.RunPassAsync(DateTime.UtcNow, CancellationToken.None);
		}

		// The formatted message embeds the swept log's ref ("{ProjectKey}/{marker}") — a unique
		// substring to find THIS pass's record among any concurrent self-log traffic.
		var rows = await WaitForRowsAsync($"events | where Message contains \"{marker}\" | take 1");
		rows.Should().ContainSingle();
		rows[0].Should().ContainKey("Invoker").WhoseValue.Should().Be("background:RetentionService");
	}

	[Fact]
	public async Task OrdinaryLoggerCall_OutsideAnyBackgroundScope_HasNoInvokerProperty()
	{
		var marker = Guid.NewGuid().ToString("N");
		using (var scope = _factory.Services.CreateScope())
		{
			var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
			var logger = loggerFactory.CreateLogger("PetBox.Test.BackgroundInvokerHostTests");
			logger.LogInformation("ordinary event {Marker}", marker);
		}

		// The distinguishing check itself: a record produced outside any BackgroundInvokerScope
		// must carry no Invoker property at all — readable from the record, no EventId lookup.
		var rows = await WaitForRowsAsync($"events | where Message contains \"{marker}\" | take 1");
		rows.Should().ContainSingle();
		rows[0].Should().NotContainKey("Invoker",
			"a record logged outside a BackgroundInvokerScope must carry no Invoker property");
	}
}
