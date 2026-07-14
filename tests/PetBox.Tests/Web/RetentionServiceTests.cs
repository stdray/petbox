using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Retention;

namespace PetBox.Tests.Web;

// spec log-retention-cascade: a named log MAY carry its own retention window, which
// RetentionService consults BEFORE the project/workspace/system cascade (RetentionService.cs).
// These tests pin both halves of the contract on the real sweep pass (RunPassAsync), not just
// the model:
//   - a log WITH RetentionDays is swept by ITS OWN window, regardless of what the cascade says.
//   - a log WITHOUT one (RetentionDays NULL — the default for every log, old and new) is swept by
//     the cascade exactly as before this column existed: a regression guard on current behavior.
public sealed class RetentionServiceTests : IAsyncLifetime
{
	const string TestProjectKey = "retention-proj";

	readonly WebApplicationFactory<Program> _factory;

	public RetentionServiceTests()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		// See TestSchema.NewTempConnectionString: WebApplication.CreateBuilder reads
		// ASPNETCORE_ENVIRONMENT at construction, before WithWebHostBuilder gets a chance —
		// without this, Features:Logging can read false on Linux CI and ILogStore never registers.
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Logging"] = "true",
						// The sweep is driven manually via RunPassAsync below — the hosted loop
						// (30s grace + its own timer) would just be dead weight in-process.
						["Host:BackgroundServices"] = "false",
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using var scope = _factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new Workspace { Key = "ws-retention", Name = "WS", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "ws-retention", Name = "Retention" });
	}

	public async Task DisposeAsync() => await _factory.DisposeAsync();

	static LogEntryRecord Entry(DateTime ts) => new()
	{
		ServiceKey = "svc",
		TimestampMs = new DateTimeOffset(ts, TimeSpan.Zero).ToUnixTimeMilliseconds(),
		Level = 0,
		Message = "m",
		MessageTemplate = "m",
		PropertiesJson = "{}",
	};

	[Fact]
	public async Task RunPassAsync_LogWithOwnWindow_SweptByItsOwnWindow_NotByTheCascade()
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();

		// Own window: 1 day. The project cascade default (LogSettings.RetentionDays) is 7 days — if
		// the cascade were consulted instead of the log's own window, this event would survive.
		await store.CreateAsync(TestProjectKey, "short", null, retentionDays: 1);

		var now = DateTime.UtcNow;
		using (var logDb = store.NewEnsuredContext(TestProjectKey, "short"))
			await logDb.InsertAsync(Entry(now.AddDays(-2)));

		var service = ActivatorUtilities.CreateInstance<RetentionService>(_factory.Services);
		await service.RunPassAsync(now, CancellationToken.None);

		using var check = store.NewEnsuredContext(TestProjectKey, "short");
		(await check.LogEntries.CountAsync())
			.Should().Be(0, "the log's own 1-day window should have swept a 2-day-old event");
	}

	[Fact]
	public async Task RunPassAsync_LogWithoutOwnWindow_SweptByTheCascade_Unchanged()
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();

		// No override — RetentionDays stays NULL. Cascade default (LogSettings.RetentionDays) is 7 days.
		await store.CreateAsync(TestProjectKey, "plain", null);

		var now = DateTime.UtcNow;
		using (var logDb = store.NewEnsuredContext(TestProjectKey, "plain"))
			await logDb.InsertAsync(Entry(now.AddDays(-2)));

		var service = ActivatorUtilities.CreateInstance<RetentionService>(_factory.Services);
		await service.RunPassAsync(now, CancellationToken.None);

		using var check = store.NewEnsuredContext(TestProjectKey, "plain");
		(await check.LogEntries.CountAsync()).Should().Be(1,
			"a 2-day-old event is well inside the 7-day cascade default and must survive, " +
			"exactly as before this column existed");
	}

	[Fact]
	public async Task UpdateRetentionDaysAsync_ThenSweep_UsesTheNewWindow()
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();

		// Created with no override (cascade default 7 days), then given a 1-day override via the
		// same patch idiom log_update exposes (LogStore.UpdateRetentionDaysAsync).
		await store.CreateAsync(TestProjectKey, "patched", null);
		var patched = await store.UpdateRetentionDaysAsync(TestProjectKey, "patched", 1);
		patched.Should().NotBeNull();
		patched!.RetentionDays.Should().Be(1);

		var now = DateTime.UtcNow;
		using (var logDb = store.NewEnsuredContext(TestProjectKey, "patched"))
			await logDb.InsertAsync(Entry(now.AddDays(-2)));

		var service = ActivatorUtilities.CreateInstance<RetentionService>(_factory.Services);
		await service.RunPassAsync(now, CancellationToken.None);

		using var check = store.NewEnsuredContext(TestProjectKey, "patched");
		(await check.LogEntries.CountAsync())
			.Should().Be(0, "the patched 1-day window should have swept a 2-day-old event");
	}

	[Fact]
	public async Task UpdateRetentionDaysAsync_ZeroClearsTheOverride_RevertsToCascade()
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();

		await store.CreateAsync(TestProjectKey, "cleared", null, retentionDays: 1);
		var cleared = await store.UpdateRetentionDaysAsync(TestProjectKey, "cleared", 0);
		cleared.Should().NotBeNull();
		cleared!.RetentionDays.Should().BeNull();
	}

	[Fact]
	public async Task CreateAsync_WithoutRetentionDays_DefaultsToNull()
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();

		var meta = await store.CreateAsync(TestProjectKey, "defaulted", null);
		meta.RetentionDays.Should().BeNull();

		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		var row = await db.Logs.FirstAsync(l => l.ProjectKey == TestProjectKey && l.Name == "defaulted");
		row.RetentionDays.Should().BeNull();
	}
}
