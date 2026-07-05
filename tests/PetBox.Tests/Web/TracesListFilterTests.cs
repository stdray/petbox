using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Tracing;
using PetBox.Web.Pages.Logs;

namespace PetBox.Tests.Web;

// spec ui-list-pagination: the traces page gains a log selector, an Error-level filter and
// server-side paging. This drives TracesModel.OnGetAsync over a real LogStore so the three
// promises are observable: the selector picks which log's spans are read, ?errorsOnly=true
// keeps only traces whose worst span status is Error, and paging applies OFFSET at the query.
public sealed class TracesListFilterTests : IDisposable
{
	const string Proj = "proj";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<LogDb> _factory;
	readonly LogStore _store;

	public TracesListFilterTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-traces-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<LogDb>(Path.Combine(_dir, "logs"), Scope.Project,
			c => new LogDb(LogDb.CreateOptions(c)), LogSchema.Ensure);
		_store = new LogStore(_db, _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// One single-span trace: index drives ordering (higher = newer), status drives the filter.
	async Task SeedTrace(string log, int index, int status)
	{
		var ctx = _store.GetContext(Proj, log);
		await ctx.InsertAsync(new SpanRecord
		{
			SpanId = $"{log}-sp{index:000}",
			TraceId = $"{log}-t{index:000}",
			ParentSpanId = null,
			Name = $"root-{index}",
			StartUnixNs = index * 1_000_000_000L,
			EndUnixNs = index * 1_000_000_000L + 5_000_000L,
			StatusCode = status,
		});
	}

	TracesModel NewModel() => new(_db, _store)
	{
		WorkspaceKey = "ws",
		ProjectKey = Proj,
	};

	[Fact]
	public async Task Paging_AppliesOffset_AcrossTwoPages()
	{
		await _store.CreateAsync(Proj, "app", null);
		for (var i = 1; i <= 55; i++) await SeedTrace("app", i, status: 1);

		var p0 = NewModel();
		p0.LogName = "app";
		await p0.OnGetAsync(default);
		p0.SelectedLog.Should().Be("app");
		p0.Traces.Count.Should().Be(50); // PageSize
		p0.HasNext.Should().BeTrue();
		p0.Traces[0].TraceId.Should().Be("app-t055"); // newest first

		var p1 = NewModel();
		p1.LogName = "app";
		p1.PageNum = 1;
		await p1.OnGetAsync(default);
		p1.Traces.Count.Should().Be(5); // OFFSET 50 → the remaining 5
		p1.HasNext.Should().BeFalse();
		p1.Traces.Select(t => t.TraceId).Should().NotContain("app-t055");
	}

	[Fact]
	public async Task ErrorsOnly_KeepsOnlyErrorTraces()
	{
		await _store.CreateAsync(Proj, "app", null);
		for (var i = 1; i <= 8; i++) await SeedTrace("app", i, status: 1); // Ok
		await SeedTrace("app", 20, status: 2); // Error
		await SeedTrace("app", 21, status: 2); // Error

		var all = NewModel();
		all.LogName = "app";
		await all.OnGetAsync(default);
		all.Traces.Count.Should().Be(10);

		var errs = NewModel();
		errs.LogName = "app";
		errs.ErrorsOnly = true;
		await errs.OnGetAsync(default);
		errs.Traces.Select(t => t.TraceId).Should().BeEquivalentTo("app-t020", "app-t021");
		errs.Traces.Should().OnlyContain(t => t.WorstStatus == 2);
	}

	[Fact]
	public async Task LogSelector_ChoosesWhichLogsSpansAreRead()
	{
		await _store.CreateAsync(Proj, "app", null);
		await _store.CreateAsync(Proj, "worker", null);
		await SeedTrace("app", 1, status: 1);
		await SeedTrace("worker", 1, status: 1);
		await SeedTrace("worker", 2, status: 1);

		var m = NewModel();
		m.LogName = "worker";
		await m.OnGetAsync(default);
		m.AvailableLogs.Should().Contain(new[] { "app", "worker" });
		m.SelectedLog.Should().Be("worker");
		m.Traces.Select(t => t.TraceId).Should().BeEquivalentTo("worker-t001", "worker-t002");
	}
}
