using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tests.Memory;
using PetBox.Web.Search;

namespace PetBox.Tests.Search;

// The dead-letter must catch a POISON DOCUMENT, never a LYING-DOWN ENDPOINT.
//
// This is not a hypothetical: eight projects had no Embed route, the drain hammered on, EVERY
// document burned through its 5 attempts and was dead-lettered FOREVER, and the cursor sailed on
// past them — `search_vec rows 0, dead total 24`. The attempt counter could not tell "this doc is
// unindexable" from "the embedder is unreachable", so a five-minute outage of the owner's home
// endpoint (which has already answered "No route to host" in the logs) was enough to permanently
// erase a project's semantic index.
//
// The line drawn here (SearchDegradedReason, the codes search already speaks):
//   embed-no-route / embed-transient (an open circuit breaker lands here too — the router reports an
//     all-endpoints-open chain as transient) = INFRASTRUCTURE. No attempt charged, no dead-letter,
//     cursor HELD, pass stalled. The document is innocent and will index on recovery.
//   embed-upstream-4xx (the provider looked at THIS text and refused it) / index-error / anything
//     else = POISON DOCUMENT. Attempts, then the dead-letter — the head-of-line protection stays.
public sealed class VectorizationOutageTests
{
	const string DocScope = "proj/notes";
	const string IndexName = "vec";

	static SearchDoc Doc(string id) => new(DocScope, "note", id, id + " text");

	// THE key test: it replays the production catastrophe. The embedder is down (transient) and the
	// drain runs 10 times — more than the 5 attempts that used to be fatal, i.e. 10 minutes of a
	// 60s-tick outage. Nothing may die, nothing may be charged, the cursor may not move; and when the
	// endpoint comes back the whole backlog indexes.
	[Fact]
	public async Task EndpointDownForTenPasses_NothingDies_NothingIsCharged_CursorHeld_ThenBackfills()
	{
		var source = new FakeSource { Upserts = [Doc("a"), Doc("b"), Doc("c")], Version = 7 };
		var index = new FakeIndex { Fail = Transient };
		var store = new CountingCursorStore();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store, maxAttempts: 5);

		for (var pass = 1; pass <= 10; pass++)
		{
			var r = await worker.DrainAsync();
			r.Indexed.Should().Be(0);
			r.DeadLettered.Should().Be(0, "pass {0}: the endpoint is down — the DOCUMENTS are fine", pass);
			r.Advanced.Should().BeFalse();
			r.Cursor.Should().Be(0, "pass {0}: the cursor must not move past docs that were never indexed", pass);
			r.Stalled.Should().Be(SearchDegradedReason.EmbedTransient);
		}

		store.Bumps.Should().Be(0, "an unreachable embedder says NOTHING about the document");
		store.Deaths.Should().Be(0);
		store.Dead.Should().BeEmpty();

		// The endpoint comes back up: the untouched delta drains forward = automatic backfill.
		index.Fail = null;
		var up = await worker.DrainAsync();
		up.Indexed.Should().Be(3);
		up.Advanced.Should().BeTrue();
		up.Cursor.Should().Be(7);
		up.Stalled.Should().BeNull();
		index.Indexed.Select(x => x.Id).Should().BeEquivalentTo(["a", "b", "c"]);
	}

	// The literal production shape: no Embed route configured for the project. Structural, not a blip
	// — but still not the document's fault, so it waits for the route instead of dying.
	[Fact]
	public async Task NoRoute_NothingDies_CursorHeld_ThenIndexesWhenTheRouteAppears()
	{
		var source = new FakeSource { Upserts = [Doc("a"), Doc("b")], Version = 5 };
		var index = new FakeIndex { Fail = NoRoute };
		var store = new CountingCursorStore();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store, maxAttempts: 5);

		for (var pass = 1; pass <= 10; pass++)
		{
			var r = await worker.DrainAsync();
			r.DeadLettered.Should().Be(0);
			r.Cursor.Should().Be(0);
			r.Stalled.Should().Be(SearchDegradedReason.EmbedNoRoute);
		}
		store.Bumps.Should().Be(0);
		store.Dead.Should().BeEmpty();

		index.Fail = null; // the LLM registry is inherited → the route exists now
		var up = await worker.DrainAsync();
		up.Indexed.Should().Be(2);
		up.Cursor.Should().Be(5);
	}

	// An open circuit breaker is reported by the router as an all-providers-failed TRANSIENT chain,
	// so it lands in the infrastructure bucket too — it used to be one more way to dead-letter a
	// perfectly good document.
	[Fact]
	public async Task OpenCircuitBreaker_IsInfrastructure_NotPoison()
	{
		var source = new FakeSource { Upserts = [Doc("a")], Version = 2 };
		// What CapabilityRouter actually throws when every endpoint's breaker is open: no call is made
		// and the chain ends as LlmRouterException(transient: true) → embed-transient at the adapter.
		var breakerOpen = new LlmRouterException(LlmCapability.Embed, transient: true,
			"all 1 Embed provider(s) failed (last attempt 0)");
		var index = new FakeIndex
		{
			Fail = () => throw new SearchDegradedException(
				SearchDegradedReason.Embed(breakerOpen.NoRoute, breakerOpen.Transient), breakerOpen.Message, breakerOpen),
		};
		var store = new CountingCursorStore();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store, maxAttempts: 2);

		await worker.DrainAsync();
		var second = await worker.DrainAsync(); // would have been the fatal one under maxAttempts: 2

		second.DeadLettered.Should().Be(0);
		second.Stalled.Should().Be(SearchDegradedReason.EmbedTransient);
		store.Bumps.Should().Be(0);
		store.Dead.Should().BeEmpty();
	}

	// The other half of the contract: the head-of-line protection is INTACT. A doc the provider itself
	// refused (4xx: too long, bad encoding, bad request) still burns its attempts and is dead-lettered,
	// so it can never block the cursor forever.
	[Fact]
	public async Task PoisonDocument_Upstream4xx_StillBurnsAttempts_AndIsDeadLettered()
	{
		var source = new FakeSource { Upserts = [Doc("good"), Doc("poison")], Version = 5 };
		var index = new FakeIndex { FailIds = { ["poison"] = Upstream4xx } };
		var store = new CountingCursorStore();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store, maxAttempts: 5);

		for (var pass = 1; pass <= 4; pass++)
		{
			var r = await worker.DrainAsync();
			r.DeadLettered.Should().Be(0, "pass {0}: still under maxAttempts", pass);
			r.Advanced.Should().BeFalse();
			r.Cursor.Should().Be(0);
			r.Stalled.Should().BeNull("a 4xx is the DOCUMENT's fault, not an outage");
		}
		store.Bumps.Should().Be(4, "the attempt counter is exactly what a poison doc is for");

		var fifth = await worker.DrainAsync();
		fifth.DeadLettered.Should().Be(1);
		fifth.Advanced.Should().BeTrue("the poison doc is out of the way — the cursor is free again");
		fifth.Cursor.Should().Be(5);
		(await store.IsDeadAsync(IndexName, "note", "poison")).Should().BeTrue();
		index.Indexed.Select(x => x.Id).Should().Contain("good");
	}

	// An index-level failure (SQL error, corrupt row) is likewise about THIS doc, and keeps dead-lettering.
	[Fact]
	public async Task PoisonDocument_IndexError_StillDeadLetters()
	{
		var source = new FakeSource { Upserts = [Doc("bad")], Version = 5 };
		var index = new FakeIndex { FailIds = { ["bad"] = IndexError } };
		var store = new CountingCursorStore();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store, maxAttempts: 2);

		await worker.DrainAsync();
		var second = await worker.DrainAsync();

		second.DeadLettered.Should().Be(1);
		store.Bumps.Should().Be(2);
		(await store.IsDeadAsync(IndexName, "note", "bad")).Should().BeTrue();
	}

	// A doc that fails 4x transiently and THEN meets a 4xx is dead-lettered on the 4xx's own count —
	// the outage passes left no residue on it.
	[Fact]
	public async Task OutageLeavesNoResidue_OnTheAttemptCounterOfADocThatLaterTurnsPoisonous()
	{
		var source = new FakeSource { Upserts = [Doc("x")], Version = 5 };
		var index = new FakeIndex { Fail = Transient };
		var store = new CountingCursorStore();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store, maxAttempts: 5);

		for (var i = 0; i < 4; i++) await worker.DrainAsync();
		store.Bumps.Should().Be(0);

		index.Fail = null;
		index.FailIds["x"] = Upstream4xx; // the endpoint is up; this doc is genuinely unindexable
		for (var i = 0; i < 4; i++) (await worker.DrainAsync()).DeadLettered.Should().Be(0);
		var fatal = await worker.DrainAsync();
		fatal.DeadLettered.Should().Be(1, "5 REAL attempts, not 1 real + 4 stolen by the outage");
	}

	// ---- the job gate: a down endpoint must not even START a drain ----

	// With Embed unavailable for the project (no route / breaker open) the pass is skipped whole:
	// no doc is walked into the dead socket, no attempt, no dead-letter row, cursor untouched.
	// The embedder here THROWS if called, so "nothing happened" is proof the gate held.
	[Fact]
	public async Task MemoryVectorizationJob_DoesNotDrain_WhenEmbedIsUnavailable()
	{
		using var fx = new JobFixture();
		await fx.SeedMemoryAsync();

		await new MemoryVectorizationJob(fx.NewMemoryFactory(), fx.Catalog, new UnavailableLlmClient())
			.DrainAllAsync(CancellationToken.None);

		fx.MemoryCount("SELECT COUNT(*) FROM search_vec").Should().Be(0);
		fx.MemoryCount("SELECT COUNT(*) FROM search_deadletter").Should().Be(0, "the endpoint was down, not the data");
		fx.MemoryCount("SELECT COALESCE(MAX(Version), 0) FROM search_cursor").Should().Be(0);

		// The endpoint comes back → the same untouched backlog indexes.
		await new MemoryVectorizationJob(fx.NewMemoryFactory(), fx.Catalog, new FakeLlmClient())
			.DrainAllAsync(CancellationToken.None);
		fx.MemoryCount("SELECT COUNT(*) FROM search_vec").Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task TasksVectorizationJob_DoesNotDrain_WhenEmbedIsUnavailable()
	{
		using var fx = new JobFixture();
		await fx.SeedTasksAsync();

		await new TasksVectorizationJob(fx.NewTasksFactory(), fx.Catalog, new UnavailableLlmClient())
			.DrainAllAsync(CancellationToken.None);

		fx.TasksCount("SELECT COUNT(*) FROM search_vec").Should().Be(0);
		fx.TasksCount("SELECT COUNT(*) FROM search_deadletter").Should().Be(0);
		fx.TasksCount("SELECT COALESCE(MAX(Version), 0) FROM search_cursor").Should().Be(0);

		await new TasksVectorizationJob(fx.NewTasksFactory(), fx.Catalog, new FakeLlmClient())
			.DrainAllAsync(CancellationToken.None);
		fx.TasksCount("SELECT COUNT(*) FROM search_vec").Should().BeGreaterThan(0);
	}

	// ---- fakes ----

	static SearchDegradedException Degraded(string reason) =>
		new(reason, "simulated " + reason);

	static Action Transient => () => throw Degraded(SearchDegradedReason.EmbedTransient);
	static Action NoRoute => () => throw Degraded(SearchDegradedReason.EmbedNoRoute);
	static Action Upstream4xx => () => throw Degraded(SearchDegradedReason.EmbedUpstream4xx);
	static Action IndexError => () => throw new InvalidOperationException("no such column: Vec");

	sealed class FakeSource : ISearchSource
	{
		public List<SearchDoc> Upserts = [];
		public long Version = 1;

		public Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default) =>
			Task.FromResult(sinceVersion >= Version
				? new SourceDelta([], [], Version)
				: new SourceDelta(Upserts, [], Version));
	}

	// Fails the whole index (`Fail`) or specific docs (`FailIds`) with a chosen exception — the
	// distinction the worker is now supposed to make.
	sealed class FakeIndex : ISearchIndex
	{
		public Action? Fail;
		public Dictionary<string, Action> FailIds = [];
		public List<(string Type, string Id)> Indexed = [];

		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;

		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default)
		{
			Fail?.Invoke();
			if (FailIds.TryGetValue(doc.Id, out var fail)) fail();
			Indexed.Add((doc.Type, doc.Id));
			return Task.CompletedTask;
		}

		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
			Task.CompletedTask;
		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) =>
			Task.CompletedTask;
		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<Hit>>([]);
	}

	// Counts what the worker CHARGES to a document — the two writes that killed the vectors.
	sealed class CountingCursorStore : IIndexCursorStore
	{
		readonly InMemoryIndexCursorStore _inner = new();
		public int Bumps;
		public int Deaths;
		public List<(string Type, string Id)> Dead = [];

		public Task<long> GetCursorAsync(string index, CancellationToken ct = default) => _inner.GetCursorAsync(index, ct);
		public Task SetCursorAsync(string index, long version, CancellationToken ct = default) => _inner.SetCursorAsync(index, version, ct);

		public Task<int> BumpAttemptsAsync(string index, string type, string id, CancellationToken ct = default)
		{
			Bumps++;
			return _inner.BumpAttemptsAsync(index, type, id, ct);
		}

		public Task ClearAttemptsAsync(string index, string type, string id, CancellationToken ct = default) =>
			_inner.ClearAttemptsAsync(index, type, id, ct);

		public Task MarkDeadAsync(string index, string type, string id, CancellationToken ct = default)
		{
			Deaths++;
			Dead.Add((type, id));
			return _inner.MarkDeadAsync(index, type, id, ct);
		}

		public Task<bool> IsDeadAsync(string index, string type, string id, CancellationToken ct = default) =>
			_inner.IsDeadAsync(index, type, id, ct);
	}

	// Embed is not available for this project (no route, or every endpoint's breaker is open) — and
	// EmbedAsync throws, so any drain that starts anyway is caught red-handed.
	sealed class UnavailableLlmClient : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new LlmRouterException(LlmCapability.Embed, transient: false, "no route configured for Embed",
				noRoute: true);
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(false);
	}

	// Real memory/tasks files + core catalog, so the job under test is the production one.
	sealed class JobFixture : IDisposable
	{
		public const string Proj = "proj";

		readonly string _dir;
		readonly PetBoxDb _db;

		public JobFixture()
		{
			_dir = Path.Combine(Path.GetTempPath(), "petbox-vecoutage-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_dir);
			var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
			TestSchema.Core(cs);
			_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
			_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
			Catalog = new ProjectCatalog(_db);
		}

		public ProjectCatalog Catalog { get; }

		public ScopedDbFactory<MemoryDb> NewMemoryFactory() =>
			new(Path.Combine(_dir, "memory"), Scope.Project, c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);

		public ScopedDbFactory<TasksDb> NewTasksFactory() =>
			new(Path.Combine(_dir, "tasks"), Scope.Project, c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);

		public async Task SeedMemoryAsync()
		{
			var memory = new MemoryService(new MemoryStore(_db, NewMemoryFactory()));
			var r = await memory.UpsertAsync(Proj, "notes",
				[new MemoryEntryInput { Key = "k1", Type = "Project", Body = "some body text" }], []);
			r.Result.Applied.Should().BeTrue();
		}

		public async Task SeedTasksAsync()
		{
			var factory = NewTasksFactory();
			var tasks = new TasksService(new TaskBoardStore(_db, factory), new RelationStore(factory),
				new TagStore(factory), new CommentService(factory), llm: null);
			await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
			await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "n1", Version = 0, Title = "t", Body = "some body text" }]);
		}

		public int MemoryCount(string sql)
		{
			using var db = new MemoryDb(MemoryDb.CreateOptions($"Data Source={Path.Combine(_dir, "memory", Proj + ".db")}"));
			return db.Execute<int>(sql);
		}

		public int TasksCount(string sql)
		{
			using var db = new TasksDb(TasksDb.CreateOptions($"Data Source={Path.Combine(_dir, "tasks", Proj + ".db")}"));
			return db.Execute<int>(sql);
		}

		public void Dispose()
		{
			_db.Dispose();
			TestDirs.CleanupOrDefer(_dir);
		}
	}
}
