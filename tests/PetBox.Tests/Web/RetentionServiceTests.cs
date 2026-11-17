using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Metrics;
using PetBox.Log.Core.Retention;
using PetBox.Log.Core.Tracing;

namespace PetBox.Tests.Web;

// spec log-retention-cascade: a named log MAY carry its own retention window, which
// RetentionService consults BEFORE the project/workspace/system cascade (RetentionService.cs).
// These tests pin both halves of the contract on the real sweep pass (RunPassAsync), not just
// the model:
//   - a log WITH RetentionDays is swept by ITS OWN window, regardless of what the cascade says.
//   - a log WITHOUT one (RetentionDays NULL — the default for every log, old and new) is swept by
//     the cascade exactly as before this column existed: a regression guard on current behavior.
// Shared per-class host (work share-fixtures-across-per-test-classes): xUnit news the test
// class per test, so without this fixture each of the 5 tests boots its own WebApplicationFactory.
// No per-test reset needed — every test names its OWN log ("short"/"plain"/"patched"/"cleared"/
// "defaulted") and only ever asserts on entries/metadata scoped to that name, so leftover rows
// from an earlier test in a different log are invisible (the ModuleViewsFixture pattern: safe by
// non-colliding entity names, not by wiping state).
public sealed class RetentionServiceFixture : IAsyncLifetime
{
	public const string TestProjectKey = "retention-proj";

	public WebApplicationFactory<Program> Factory { get; }

	public RetentionServiceFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		// See TestSchema.NewTempConnectionString: WebApplication.CreateBuilder reads
		// ASPNETCORE_ENVIRONMENT at construction, before WithWebHostBuilder gets a chance —
		// without this, Features:Logging can read false on Linux CI and ILogStore never registers.
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		Factory = new WebApplicationFactory<Program>()
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

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new Workspace { Key = "ws-retention", Name = "WS", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "ws-retention", Name = "Retention" });
	}

	public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class RetentionServiceTests : IClassFixture<RetentionServiceFixture>
{
	const string TestProjectKey = RetentionServiceFixture.TestProjectKey;

	readonly WebApplicationFactory<Program> _factory;

	public RetentionServiceTests(RetentionServiceFixture fx)
	{
		_factory = fx.Factory;
	}

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

	// spec log-retention-cascade as it is actually meant: the window belongs to the LOG, so every
	// signal stored in that log is swept by it — not just events. Guards two things at once: the
	// sweep itself, and the `> 0` predicate that keeps an UNSTAMPED row (a producer that omits the
	// OTLP time field lands at 0) from being deleted as though it were ancient.
	[Fact]
	public async Task RunPassAsync_SweepsSpansAndMetricPoints_ByTheLogsOwnWindow()
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		await store.CreateAsync(TestProjectKey, "signals", null, retentionDays: 1);

		var now = DateTime.UtcNow;
		var oldNs = Nanos(now.AddDays(-2));
		var freshNs = Nanos(now.AddHours(-1));

		using (var logDb = store.NewEnsuredContext(TestProjectKey, "signals"))
		{
			await logDb.InsertAsync(Entry(now.AddDays(-2)));
			await logDb.InsertAsync(Span("old", oldNs));
			await logDb.InsertAsync(Span("fresh", freshNs));
			await logDb.InsertAsync(Span("unstamped", 0));
			await logDb.InsertAsync(Point("old", oldNs));
			await logDb.InsertAsync(Point("fresh", freshNs));
			await logDb.InsertAsync(Point("unstamped", 0));
		}

		var service = ActivatorUtilities.CreateInstance<RetentionService>(_factory.Services);
		await service.RunPassAsync(now, CancellationToken.None);

		using var check = store.NewEnsuredContext(TestProjectKey, "signals");
		(await check.LogEntries.CountAsync()).Should().Be(0);
		(await check.Spans.CountAsync()).Should().Be(2,
			"the 2-day-old span is outside the log's 1-day window; the fresh one and the " +
			"unstamped one (time 0) both survive");
		(await check.MetricPoints.CountAsync()).Should().Be(2,
			"same window, same unstamped guard, for metric points");
	}

	// Regression guard for the lock-out that this sweep caused on 2026-11-17: the sweep runs in
	// bounded batches, so a table holding MORE stale rows than one batch must still empty completely
	// (the loop must not stop after the first batch). Batching is what keeps the log's writers — and
	// `/v1/traces` behind them — from waiting on a million-row transaction.
	[Fact]
	public async Task RunPassAsync_SweepsMoreStaleRowsThanOneBatch()
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		await store.CreateAsync(TestProjectKey, "bulk", null, retentionDays: 1);

		var now = DateTime.UtcNow;
		var oldNs = Nanos(now.AddDays(-2));

		using (var logDb = store.NewEnsuredContext(TestProjectKey, "bulk"))
		{
			for (var i = 0; i < 1_200; i++)
				await logDb.InsertAsync(Span("bulk-" + i, oldNs));
		}

		var service = ActivatorUtilities.CreateInstance<RetentionService>(_factory.Services);
		await service.RunPassAsync(now, CancellationToken.None);

		using var check = store.NewEnsuredContext(TestProjectKey, "bulk");
		(await check.Spans.CountAsync()).Should().Be(0,
			"1 200 stale spans is more than two batches — the loop has to keep going until none is left");
	}

	static long Nanos(DateTime ts) =>
		new DateTimeOffset(ts, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000;

	static SpanRecord Span(string name, long startNs) => new()
	{
		SpanId = "span-" + name,
		TraceId = "trace-" + name,
		Name = name,
		StartUnixNs = startNs,
		EndUnixNs = startNs,
	};

	static MetricPointRecord Point(string name, long timeNs) => new()
	{
		MetricName = "metric-" + name,
		TimeUnixNs = timeNs,
	};
}
