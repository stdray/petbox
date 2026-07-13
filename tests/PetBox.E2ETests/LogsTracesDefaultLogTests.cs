using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Tracing;

namespace PetBox.E2ETests;

// work logs-traces-default-log: full-stack proof that the Logs/Traces pages preselect a log and
// distinguish "no logs" from "a log with no data" from "a log with data" — see
// DefaultLogSelectorTests (unit) and TracesListFilterTests/LogsIndexDefaultLogTests (page-model)
// for the same rule pinned closer to the code. This class exercises the real HTTP + Razor
// rendering, including the testids the maintainer's own browser would have hit.
[Collection(nameof(UiCollection))]
public sealed class LogsTracesDefaultLogTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	// Each test uses its OWN project (never $system, which the whole UI collection shares) so
	// seeding logs here can't race or interfere with other files in the collection.
	const string Ws = "logdeflog-ws";
	const string ProjNoLogs = "logdeflog-empty";
	const string ProjMulti = "logdeflog-multi";

	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == ProjNoLogs))
			await db.InsertAsync(new Project { Key = ProjNoLogs, WorkspaceKey = Ws, Name = "No Logs" });
		if (!await db.Projects.AnyAsync(p => p.Key == ProjMulti))
			await db.InsertAsync(new Project { Key = ProjMulti, WorkspaceKey = Ws, Name = "Multi Log" });

		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		if (!await store.ExistsAsync(ProjMulti, "zzz-old"))
		{
			// Created FIRST (oldest) — alphabetically LAST. Holds the real data, exactly like
			// $system/petbox in production: an established log whose name happens to sort after
			// a newer, empty one.
			await store.CreateAsync(ProjMulti, "zzz-old", "the established log");
			var ctx = store.GetContext(ProjMulti, "zzz-old");
			await ctx.InsertAsync(new LogEntryRecord
			{
				ServiceKey = "svc",
				TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Level = 2,
				Message = "the real event",
				MessageTemplate = "the real event",
			});
			await ctx.InsertAsync(new SpanRecord
			{
				SpanId = "sp1",
				TraceId = "t1",
				ParentSpanId = null,
				Name = "root",
				StartUnixNs = 1_000_000_000L,
				EndUnixNs = 1_005_000_000L,
				StatusCode = 1,
			});

			// Created SECOND (newer) — alphabetically FIRST, and deliberately empty: this is the
			// log the OLD "alphabetically first" rule would have picked.
			await store.CreateAsync(ProjMulti, "aaa-new", "newer, empty log");
		}

		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	[Fact]
	public async Task LogsPage_NoQueryString_SelectsTheOldestLog_AndShowsItsEvents()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{ProjMulti}/logs");

		await Expect(_page.GetByTestId("log-tab").Filter(new() { HasText = "zzz-old" }))
			.ToHaveClassAsync(new Regex("btn-primary"));
		await Expect(_page.GetByTestId("events-body")).ToContainTextAsync("the real event");
	}

	[Fact]
	public async Task TracesPage_NoQueryString_SelectsTheOldestLog_AndShowsItsTraces()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{ProjMulti}/traces");

		await Expect(_page.GetByTestId("traces-log-select")).ToHaveValueAsync("zzz-old");
		await Expect(_page.GetByTestId("traces-table")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("trace-row")).ToHaveCountAsync(1);
	}

	// The actual defect this work fixes: "no logs in the project" must NOT render the same blank
	// as "a log exists but has no data" (Model.NoLogs vs. Model.SchemaMissing/Events.Count == 0).
	[Fact]
	public async Task LogsPage_NoLogsInProject_ShowsDistinctEmptyState()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{ProjNoLogs}/logs");

		await Expect(_page.GetByTestId("logs-none")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("logs-create-link")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("events-empty")).ToHaveCountAsync(0);
	}

	[Fact]
	public async Task TracesPage_NoLogsInProject_ShowsDistinctEmptyState()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{ProjNoLogs}/traces");

		await Expect(_page.GetByTestId("traces-none")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("traces-create-link")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("traces-empty")).ToHaveCountAsync(0);
	}
}
