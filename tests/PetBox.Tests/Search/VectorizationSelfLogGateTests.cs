using LinqToDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tests.Memory;
using PetBox.Web.Search;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PetBox.Tests.Search;

// work vectorization-jobs-flood-selflog: MemoryVectorizationJob/TasksVectorizationJob used to log
// their per-project stats line (EventId 410/411) at Information on EVERY tick for EVERY project,
// regardless of whether the pass did anything — 98.25% of the self-log `petbox`'s 30-day volume was
// exactly this ("indexed 0, dead-lettered 0, lag 0"). The fix: log at Debug (the self-log's global
// MinimumLevel=Information then drops it) unless there is a signal (Indexed>0 or DeadLettered>0 or
// Lag>0), PLUS one Information heartbeat line per hour so a fully-quiet job stays distinguishable
// from a dead one. These tests pin both halves — the gate must not fire on nothing, and it must
// never swallow a real signal — plus the heartbeat's cadence and per-PASS (not per-project) unit.
public sealed class VectorizationSelfLogGateTests : IDisposable
{
	const string Proj = "proj";
	const string Proj2 = "proj2";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ProjectCatalog _catalog;

	public VectorizationSelfLogGateTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-selflog-gate-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_catalog = new ProjectCatalog(_db.Factory());
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		try { Directory.Delete(_dir, recursive: true); } catch { }
	}

	sealed record Entry(MsLogLevel Level, int EventId, string Message);

	sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<Entry> Entries { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add(new Entry(logLevel, eventId.Id, formatter(state, exception)));
	}

	// ---- Memory fixtures ----

	ScopedDbFactory<MemoryDb> MemFactory() =>
		new(Path.Combine(_dir, "memory"), Scope.Project, c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);

	async Task SeedMemoryEntryAsync(ScopedDbFactory<MemoryDb> factory, string project = Proj, string store = "notes")
	{
		var memory = new MemoryService(new MemoryStore(_db.Factory(), factory));
		var r = await memory.UpsertAsync(project, store,
			[new MemoryEntryInput { Key = "k1", Type = "Project", Body = "some body text" }], []);
		Assert.True(r.Result.Applied);
	}

	// ---- Tasks fixtures ----

	ScopedDbFactory<TasksDb> TasksFactory() =>
		new(Path.Combine(_dir, "tasks"), Scope.Project, c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);

	async Task SeedTaskNodeAsync(ScopedDbFactory<TasksDb> factory, string project = Proj, string board = "b")
	{
		var boards = new TaskBoardStore(_db.Factory(), factory);
		var tasks = new TasksService(boards, new RelationStore(factory), new TagStore(factory),
			new CommentService(factory), llm: null);
		await tasks.CreateBoardAsync(project, board, "simple", null, null);
		var r = await tasks.UpsertAsync(project, board,
			[new NodePatch { Key = "n1", Version = 0, Title = "t", Body = "some body text" }]);
		Assert.True(r.Result.Applied);
	}

	// ==== Gate: MemoryVectorizationJob (EventId 410) ====

	// THE red-proof scenario. Pass 1 indexes the one entry (Indexed=1 — a real signal, must stay
	// Information). Pass 2 (a fresh job instance, same factory/catalog — the next 60s tick) has
	// nothing left to do: Indexed=0, DeadLettered=0, Lag=0. Before the fix, pass 2 ALSO logged
	// Information ("indexed 0, dead-lettered 0, lag 0") — exactly the 93% flood this card is about.
	[Fact]
	public async Task Memory_SteadyStatePass_GatesToDebug_WhileSignalPassStaysInformation()
	{
		var factory = MemFactory();
		await SeedMemoryEntryAsync(factory);

		var log1 = new CapturingLogger<MemoryVectorizationJob>();
		await new MemoryVectorizationJob(MemFactory(), _catalog, new FakeLlmClient(), log1).DrainAllAsync(CancellationToken.None);
		var pass1 = log1.Entries.Should().ContainSingle(e => e.EventId == 410).Subject;
		pass1.Level.Should().Be(MsLogLevel.Information, "Indexed=1 is a real signal");
		pass1.Message.Should().Contain("indexed 1");

		var log2 = new CapturingLogger<MemoryVectorizationJob>();
		await new MemoryVectorizationJob(MemFactory(), _catalog, new FakeLlmClient(), log2).DrainAllAsync(CancellationToken.None);
		var pass2 = log2.Entries.Should().ContainSingle(e => e.EventId == 410).Subject;
		pass2.Level.Should().Be(MsLogLevel.Debug, "nothing happened this pass — this is the flood the card is about");
		log2.Entries.Should().NotContain(e => e.EventId == 410 && e.Level == MsLogLevel.Information);
	}

	// Obverse: a permanently-failing embedder dead-letters the entry after 5 attempts
	// (AsyncVectorizationWorker's default maxAttempts). The pass where DeadLettered first becomes >0
	// must NOT be silenced by the gate — a gate that swallows this would hide the exact failure mode
	// (dead-letter) the card's acceptance criteria require staying visible immediately.
	[Fact]
	public async Task Memory_DeadLetteredPass_StaysAtInformation()
	{
		var factory = MemFactory();
		await SeedMemoryEntryAsync(factory);

		CapturingLogger<MemoryVectorizationJob> log = new();
		for (var i = 0; i < 5; i++)
		{
			log = new CapturingLogger<MemoryVectorizationJob>();
			await new MemoryVectorizationJob(MemFactory(), _catalog, new ThrowingLlmClient(), log).DrainAllAsync(CancellationToken.None);
		}

		var last = log.Entries.Should().ContainSingle(e => e.EventId == 410).Subject;
		last.Message.Should().Contain("dead-lettered 1");
		last.Level.Should().Be(MsLogLevel.Information, "a dead-letter must never be gated to Debug");
	}

	// ==== Gate: TasksVectorizationJob (EventId 411) — mirrors the memory job's gate 1:1 ====

	[Fact]
	public async Task Tasks_SteadyStatePass_GatesToDebug_WhileSignalPassStaysInformation()
	{
		var factory = TasksFactory();
		await SeedTaskNodeAsync(factory);

		var log1 = new CapturingLogger<TasksVectorizationJob>();
		await new TasksVectorizationJob(TasksFactory(), _catalog, new FakeLlmClient(), log1).DrainAllAsync(CancellationToken.None);
		var pass1 = log1.Entries.Should().ContainSingle(e => e.EventId == 411).Subject;
		pass1.Level.Should().Be(MsLogLevel.Information, "Indexed=1 is a real signal");

		var log2 = new CapturingLogger<TasksVectorizationJob>();
		await new TasksVectorizationJob(TasksFactory(), _catalog, new FakeLlmClient(), log2).DrainAllAsync(CancellationToken.None);
		var pass2 = log2.Entries.Should().ContainSingle(e => e.EventId == 411).Subject;
		pass2.Level.Should().Be(MsLogLevel.Debug, "nothing happened this pass");
		log2.Entries.Should().NotContain(e => e.EventId == 411 && e.Level == MsLogLevel.Information);
	}

	// ==== Heartbeat: MemoryVectorizationJob (EventId 415) ====

	// Anchored near real UtcNow (not FakeTimeProvider's epoch default) so a genuinely-parallel test
	// elsewhere in the run that also drives one of these jobs with the real system clock computes a
	// small, harmless gap against this test's stored heartbeat timestamp instead of a huge one — see
	// the file header on MemoryVectorizationJob.s_lastHeartbeatUtc for why this state is static
	// (must survive SearchEnrichmentService's per-tick DI scope).
	[Fact]
	public async Task Heartbeat_FiresOnFirstPass_SuppressedWithinHour_FiresAgainAfterHour()
	{
		MemoryVectorizationJob.ResetHeartbeatClockForTests();
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

		var log1 = new CapturingLogger<MemoryVectorizationJob>();
		await new MemoryVectorizationJob(MemFactory(), _catalog, new FakeLlmClient(), log1, time).DrainAllAsync(CancellationToken.None);
		log1.Entries.Should().ContainSingle(e => e.EventId == 415, "first pass ever must prove liveness immediately");

		var log2 = new CapturingLogger<MemoryVectorizationJob>();
		await new MemoryVectorizationJob(MemFactory(), _catalog, new FakeLlmClient(), log2, time).DrainAllAsync(CancellationToken.None);
		log2.Entries.Should().NotContain(e => e.EventId == 415, "same hour — must not re-fire every 60s tick");

		time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
		var log3 = new CapturingLogger<MemoryVectorizationJob>();
		await new MemoryVectorizationJob(MemFactory(), _catalog, new FakeLlmClient(), log3, time).DrainAllAsync(CancellationToken.None);
		log3.Entries.Should().ContainSingle(e => e.EventId == 415, "an hour has passed — heartbeat due again");
	}

	// The unit of the heartbeat is the JOB'S PASS, not the project: DrainAllAsync visits every
	// project on every tick, so a per-project heartbeat would be the same N-events-per-tick
	// multiplier this whole card exists to remove. Two projects, one pass, one heartbeat line.
	[Fact]
	public async Task Heartbeat_IsOncePerPass_NotOncePerProject()
	{
		MemoryVectorizationJob.ResetHeartbeatClockForTests();
		_db.Insert(new Project { Key = Proj2, WorkspaceKey = "ws", Name = "P2", Description = "" });
		var factory = MemFactory();
		await SeedMemoryEntryAsync(factory, Proj);
		await SeedMemoryEntryAsync(factory, Proj2);

		var log = new CapturingLogger<MemoryVectorizationJob>();
		await new MemoryVectorizationJob(MemFactory(), _catalog, new FakeLlmClient(), log, new FakeTimeProvider(DateTimeOffset.UtcNow))
			.DrainAllAsync(CancellationToken.None);

		log.Entries.Count(e => e.EventId == 410).Should().Be(2, "sanity: both projects were visited");
		log.Entries.Should().ContainSingle(e => e.EventId == 415, "one heartbeat for the whole pass, not one per project");
	}
}
