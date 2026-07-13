using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Log.Core.Data;
using PetBox.Web.Pages.Logs;

namespace PetBox.Tests.Web;

// logs-traces-default-log: drives IndexModel.OnGetAsync over a real LogStore (mirrors
// TracesListFilterTests, the Traces-page equivalent). Two things pinned here:
//   1. THE regression that fooled the maintainer — a project with an existing, populated log
//      renders that log's events with NO ?logName= at all, instead of a blank indistinguishable
//      from "no data".
//   2. The three states a viewer can land on must render (and test) as visibly different: no
//      logs in the project at all, a log selected but genuinely empty, and a log with events.
public sealed class LogsIndexDefaultLogTests : IDisposable
{
	const string Proj = "proj";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<LogDb> _factory;
	readonly LogStore _store;

	public LogsIndexDefaultLogTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-logs-index-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<LogDb>(Path.Combine(_dir, "logs"), Scope.Project,
			c => new LogDb(LogDb.CreateOptions(c)), LogSchema.Ensure);
		_store = new LogStore(_db.Factory(), _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	async Task SeedEvent(string log, string message, LogLevel level = LogLevel.Information)
	{
		var ctx = _store.GetContext(Proj, log);
		await ctx.InsertAsync(new LogEntryRecord
		{
			ServiceKey = "svc",
			TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Level = (int)level,
			Message = message,
			MessageTemplate = message,
		});
	}

	// PageContext must be wired: OnGetAsync reads Request.Headers (the HX-Request htmx check) on
	// its normal-path return — an unwired PageModel's Request throws NullReferenceException.
	IndexModel NewModel() => new(_store, _db.Factory().Projects(), _db.Factory().SavedQueries())
	{
		WorkspaceKey = "ws",
		ProjectKeyRoute = Proj,
		PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
	};

	[Fact]
	public async Task NoQueryString_RendersTheOnlyLogsEvents_TheMaintainerRegression()
	{
		await _store.CreateAsync(Proj, "petbox", "self-log");
		await SeedEvent("petbox", "boot complete");
		await SeedEvent("petbox", "second event");

		var m = NewModel(); // LogNameRoute left null — exactly the plain-URL case
		await m.OnGetAsync(default);

		m.NoLogs.Should().BeFalse();
		m.SchemaMissing.Should().BeFalse();
		m.SelectedLog.Should().Be("petbox");
		m.Events.Should().HaveCount(2);
	}

	// Reproduces the production shape: three logs, none named "default", none requested. The OLD
	// rule (alphabetically first) would have picked "cc-telemetry" here; the log that actually
	// has events is "petbox" (oldest).
	[Fact]
	public async Task NoQueryString_WithSeveralLogs_PicksTheOldestNotTheAlphabeticallyFirst()
	{
		await _store.CreateAsync(Proj, "petbox", null); // created FIRST (oldest) — has data below
		await _store.CreateAsync(Proj, "cc-telemetry", null); // alphabetically first, empty
		await _store.CreateAsync(Proj, "prompt-rag-audit", null);
		await SeedEvent("petbox", "the real data");

		var m = NewModel();
		await m.OnGetAsync(default);

		m.SelectedLog.Should().Be("petbox");
		m.Events.Should().ContainSingle(e => e.Message == "the real data");
	}

	[Fact]
	public async Task State_NoLogsInProject_IsDistinctFromAnEmptyLog()
	{
		var m = NewModel(); // no _store.CreateAsync call at all — project has zero logs

		await m.OnGetAsync(default);

		m.NoLogs.Should().BeTrue();
		m.SelectedLog.Should().BeNull();
		m.SchemaMissing.Should().BeFalse();
		m.Events.Should().BeEmpty();
	}

	[Fact]
	public async Task State_LogExistsButHasNoEvents_IsDistinctFromNoLogsInProject()
	{
		await _store.CreateAsync(Proj, "app", null); // schema created, never ingested into

		var m = NewModel();
		await m.OnGetAsync(default);

		m.NoLogs.Should().BeFalse("a log DOES exist in the project — this is not the same state as zero logs");
		m.SelectedLog.Should().Be("app");
		m.Events.Should().BeEmpty();
	}

	[Fact]
	public async Task State_LogWithEvents_RendersThem()
	{
		await _store.CreateAsync(Proj, "app", null);
		await SeedEvent("app", "hello");

		var m = NewModel();
		await m.OnGetAsync(default);

		m.NoLogs.Should().BeFalse();
		m.Events.Should().ContainSingle(e => e.Message == "hello");
	}
}
