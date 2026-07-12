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

// Resurrecting a BURNED index. Once an outage has dead-lettered a project's documents, two pieces
// of state conspire to keep them out of search FOREVER: the dead-letter (the worker skips them) and
// the cursor, which sailed on PAST them (so they are not in the delta any more). Fixing the embed
// route heals neither. `search_reindex` (SearchReindexService) resets BOTH, per named index, and
// then does nothing else — the stock drain re-embeds the corpus, in take-N portions.
public sealed class SearchReindexTests
{
	// ---- (a) the reset: dead-letter emptied, cursor rewound, the WHOLE backlog re-indexed ----

	[Fact]
	public async Task Reindex_ClearsDeadLetter_RewindsCursor_AndTheNextDrainIndexesTheWholeBacklog()
	{
		using var fx = new Fixture();
		await fx.SeedMemoryAsync("notes", 4);

		// Burn the index the way production burned: the embedder refuses every doc, five passes,
		// every document dead-lettered — and the cursor advances past them (nothing blocks any more).
		for (var i = 0; i < 5; i++) await fx.MemoryJob(new ThrowingLlmClient()).DrainAllAsync(default);
		fx.MemoryCount("SELECT COUNT(*) FROM search_deadletter WHERE Dead = 1").Should().Be(4);
		fx.MemoryCount("SELECT COUNT(*) FROM search_vec").Should().Be(0);
		fx.MemoryCount("SELECT COALESCE(MAX(Version), 0) FROM search_cursor").Should().BeGreaterThan(0,
			"the cursor moved past the documents it never indexed — that is the second half of the damage");

		// The route comes back. The drain alone recovers NOTHING: dead + behind the cursor.
		await fx.MemoryJob(new FakeLlmClient()).DrainAllAsync(default);
		fx.MemoryCount("SELECT COUNT(*) FROM search_vec").Should().Be(0, "a healthy embedder does not undo a dead-letter");

		var result = await fx.Reindex(new FakeLlmClient()).ReindexAsync(Fixture.Proj, ReindexTier.Memory);

		result.Tiers.Should().ContainSingle().Which.Indexes.Should().BeEquivalentTo([MemoryCursors.Vector("notes")]);
		result.Tiers[0].DeadCleared.Should().Be(4);
		result.Tiers[0].ActiveDocs.Should().Be(4, "the verification number: how many search_vec rows to expect");
		result.Tiers[0].VectorRows.Should().Be(0, "the pre-backfill baseline");
		fx.MemoryCount("SELECT COUNT(*) FROM search_deadletter").Should().Be(0);
		fx.MemoryCount("SELECT COALESCE(MAX(Version), 0) FROM search_cursor").Should().Be(0);

		// The stock drain now sees the whole store as a delta and re-embeds it.
		await fx.MemoryJob(new FakeLlmClient()).DrainAllAsync(default);
		fx.MemoryCount("SELECT COUNT(*) FROM search_vec").Should().Be(4);
		fx.MemoryCount("SELECT COUNT(*) FROM search_deadletter WHERE Dead = 1").Should().Be(0);
	}

	[Fact]
	public async Task Reindex_Tasks_ResetsTheBareBoardCursor_AndTheBoardIsReIndexed()
	{
		using var fx = new Fixture();
		await fx.SeedTasksAsync("b", 3);

		for (var i = 0; i < 5; i++) await fx.TasksJob(new ThrowingLlmClient()).DrainAllAsync(default);
		fx.TasksCount("SELECT COUNT(*) FROM search_deadletter WHERE Dead = 1").Should().Be(3);

		var result = await fx.Reindex(new FakeLlmClient()).ReindexAsync(Fixture.Proj, ReindexTier.Tasks);

		// The tasks cursor is the BARE board name — no `vector:` prefix (TasksVectorizationJob).
		result.Tiers.Should().ContainSingle().Which.Indexes.Should().BeEquivalentTo(["b"]);
		fx.TasksCount("SELECT COUNT(*) FROM search_deadletter").Should().Be(0);
		fx.TasksCount("SELECT COALESCE(MAX(Version), 0) FROM search_cursor").Should().Be(0);

		await fx.TasksJob(new FakeLlmClient()).DrainAllAsync(default);
		fx.TasksCount("SELECT COUNT(*) FROM search_vec").Should().Be(3);
	}

	// ---- (b) the gate: no Embed route → REFUSE, and reset nothing ----

	[Fact]
	public async Task Reindex_RefusesAndResetsNothing_WhenEmbedIsUnavailable()
	{
		using var fx = new Fixture();
		await fx.SeedMemoryAsync("notes", 2);
		for (var i = 0; i < 5; i++) await fx.MemoryJob(new ThrowingLlmClient()).DrainAllAsync(default);
		var deadBefore = fx.MemoryCount("SELECT COUNT(*) FROM search_deadletter WHERE Dead = 1");
		var cursorBefore = fx.MemoryCount("SELECT COALESCE(MAX(Version), 0) FROM search_cursor");
		deadBefore.Should().Be(2);
		cursorBefore.Should().BeGreaterThan(0);

		var refuse = async () => await fx.Reindex(new UnavailableLlmClient()).ReindexAsync(Fixture.Proj);

		(await refuse.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Embed*not available*");
		fx.MemoryCount("SELECT COUNT(*) FROM search_deadletter WHERE Dead = 1").Should().Be(deadBefore,
			"a refused reindex must leave the state EXACTLY as it was");
		fx.MemoryCount("SELECT COALESCE(MAX(Version), 0) FROM search_cursor").Should().Be(cursorBefore);
	}

	// ---- (c) idempotence ----

	[Fact]
	public async Task Reindex_IsIdempotent_ASecondRunIsHarmless()
	{
		using var fx = new Fixture();
		await fx.SeedMemoryAsync("notes", 3);
		await fx.MemoryJob(new FakeLlmClient()).DrainAllAsync(default);
		fx.MemoryCount("SELECT COUNT(*) FROM search_vec").Should().Be(3);

		var first = await fx.Reindex(new FakeLlmClient()).ReindexAsync(Fixture.Proj, ReindexTier.Memory);
		var second = await fx.Reindex(new FakeLlmClient()).ReindexAsync(Fixture.Proj, ReindexTier.Memory);

		first.Tiers[0].CursorsReset.Should().Be(1);
		second.Tiers[0].CursorsReset.Should().Be(0, "the cursor is already 0 — nothing left to rewind");
		second.Tiers[0].DeadCleared.Should().Be(0);
		second.Tiers[0].ActiveDocs.Should().Be(3);

		// Re-embedding the same docs is an upsert: the index converges, it does not duplicate.
		await fx.MemoryJob(new FakeLlmClient()).DrainAllAsync(default);
		fx.MemoryCount("SELECT COUNT(*) FROM search_vec").Should().Be(3);
	}

	// ---- (d) the take-N cap: a big delta drains in PORTIONS, the cursor walking forward ----

	[Fact]
	public async Task Cap_DrainsInPortions_AdvancingTheCursorEachPass()
	{
		using var fx = new Fixture();
		await fx.SeedMemoryAsync("notes", 5); // 5 separate upserts → 5 distinct versions

		DataConnection Connect() => fx.NewMemoryFactory().NewEnsuredConnection(Fixture.Proj);
		var index = new CountingIndex();
		var store = new InMemoryIndexCursorStore();
		var source = new MemorySearchSource(Connect, Fixture.Proj, "notes", maxDocs: 2);
		var worker = new AsyncVectorizationWorker(MemoryCursors.Vector("notes"), source, index, store);

		var p1 = await worker.DrainAsync();
		var p2 = await worker.DrainAsync();
		var p3 = await worker.DrainAsync();
		var p4 = await worker.DrainAsync();

		p1.Indexed.Should().Be(2, "the cap bounds ONE pass — an uncapped post-reindex delta would own the whole tick");
		p2.Indexed.Should().Be(2);
		p3.Indexed.Should().Be(1);
		p4.Indexed.Should().Be(0, "caught up");
		p1.Cursor.Should().BeLessThan(p2.Cursor).And.BeGreaterThan(0, "each portion walks the cursor forward");
		p2.Cursor.Should().BeLessThan(p3.Cursor);
		index.Ids.Should().HaveCount(5).And.OnlyHaveUniqueItems("no doc is embedded twice, none is skipped");
	}

	// A version GROUP (one upsert batch stamps every row with the same version) may not be cut in
	// half: the cursor would move past the rows left behind and strand them forever. The cap is
	// therefore soft — it takes the whole group.
	[Fact]
	public async Task Cap_NeverSplitsAVersionGroup_SoNoDocumentIsStranded()
	{
		using var fx = new Fixture();
		await fx.SeedMemoryBatchAsync("notes", 4); // ONE upsert of 4 entries → all share one version

		DataConnection Connect() => fx.NewMemoryFactory().NewEnsuredConnection(Fixture.Proj);
		var index = new CountingIndex();
		var source = new MemorySearchSource(Connect, Fixture.Proj, "notes", maxDocs: 2);
		var worker = new AsyncVectorizationWorker(MemoryCursors.Vector("notes"), source, index,
			new InMemoryIndexCursorStore());

		var pass = await worker.DrainAsync();

		pass.Indexed.Should().Be(4, "the batch shares one version — cutting it would strand the remainder");
		(await worker.DrainAsync()).Indexed.Should().Be(0);
	}

	// ---- (e) the blast radius: only the ENUMERATED indexes are touched ----

	[Fact]
	public async Task Reindex_LeavesForeignCursorsAndDeadLettersAlone()
	{
		using var fx = new Fixture();
		await fx.SeedMemoryAsync("notes", 2);
		for (var i = 0; i < 5; i++) await fx.MemoryJob(new ThrowingLlmClient()).DrainAllAsync(default);

		// A stranger in the same table: another subsystem's marker (dedup sweep / behavior mining
		// keep their own rows in search_cursor) and a dead-letter under a foreign index name. A LIKE
		// sweep would rewind them and make that subsystem replay its whole history.
		DataConnection Connect() => fx.NewMemoryFactory().NewEnsuredConnection(Fixture.Proj);
		var cursors = new SqliteIndexCursorStore(Connect);
		await cursors.SetCursorAsync("dedup-sweep", 99);
		await cursors.MarkDeadAsync("some-other-index", "Fact", "x");

		await fx.Reindex(new FakeLlmClient()).ReindexAsync(Fixture.Proj, ReindexTier.Memory);

		(await cursors.GetCursorAsync("dedup-sweep")).Should().Be(99, "a foreign cursor is NOT ours to rewind");
		(await cursors.IsDeadAsync("some-other-index", "Fact", "x")).Should().BeTrue();
		(await cursors.GetCursorAsync(MemoryCursors.Vector("notes"))).Should().Be(0);
		fx.MemoryCount("SELECT COUNT(*) FROM search_deadletter WHERE IndexName = 'vector:notes'").Should().Be(0);
	}

	// ---- fakes + fixture ----

	sealed class CountingIndex : ISearchIndex
	{
		public List<string> Ids = [];
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;

		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default)
		{
			Ids.Add(doc.Id);
			return Task.CompletedTask;
		}

		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
			Task.CompletedTask;
		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) =>
			Task.CompletedTask;
		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<Hit>>([]);
	}

	// Embed has no route for this project — IsAvailableAsync says so, and EmbedAsync throws, so a
	// reindex that started anyway would be caught red-handed.
	sealed class UnavailableLlmClient : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new LlmRouterException(LlmCapability.Embed, transient: false, "no route", noRoute: true);
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(false);
	}

	sealed class Fixture : IDisposable
	{
		public const string Proj = "proj";

		readonly string _dir;
		readonly PetBoxDb _db;
		readonly ProjectCatalog _catalog;

		public Fixture()
		{
			_dir = Path.Combine(Path.GetTempPath(), "petbox-reindex-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_dir);
			var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
			TestSchema.Core(cs);
			_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
			_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
			_catalog = new ProjectCatalog(_db.Factory());
		}

		public ScopedDbFactory<MemoryDb> NewMemoryFactory() =>
			new(Path.Combine(_dir, "memory"), Scope.Project, c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);

		public ScopedDbFactory<TasksDb> NewTasksFactory() =>
			new(Path.Combine(_dir, "tasks"), Scope.Project, c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);

		public MemoryVectorizationJob MemoryJob(ILlmClient llm) => new(NewMemoryFactory(), _catalog, llm);
		public TasksVectorizationJob TasksJob(ILlmClient llm) => new(NewTasksFactory(), _catalog, llm);

		public SearchReindexService Reindex(ILlmClient llm) =>
			new(NewMemoryFactory(), NewTasksFactory(), _catalog, llm);

		// n entries, one upsert each → n distinct versions (what the take-N cap walks through).
		public async Task SeedMemoryAsync(string store, int n)
		{
			var memory = new MemoryService(new MemoryStore(_db.Factory(), NewMemoryFactory()));
			for (var i = 0; i < n; i++)
			{
				var r = await memory.UpsertAsync(Proj, store,
					[new MemoryEntryInput { Key = $"k{i}", Type = "Project", Body = $"body number {i}" }], []);
				r.Result.Applied.Should().BeTrue();
			}
		}

		// n entries in ONE upsert → all share a single version (an indivisible group).
		public async Task SeedMemoryBatchAsync(string store, int n)
		{
			var memory = new MemoryService(new MemoryStore(_db.Factory(), NewMemoryFactory()));
			var inputs = Enumerable.Range(0, n)
				.Select(i => new MemoryEntryInput { Key = $"k{i}", Type = "Project", Body = $"body number {i}" })
				.ToList();
			var r = await memory.UpsertAsync(Proj, store, inputs, []);
			r.Result.Applied.Should().BeTrue();
		}

		public async Task SeedTasksAsync(string board, int n)
		{
			var factory = NewTasksFactory();
			var tasks = new TasksService(new TaskBoardStore(_db.Factory(), factory), new RelationStore(factory),
				new TagStore(factory), new CommentService(factory), llm: null);
			await tasks.CreateBoardAsync(Proj, board, "simple", null, null);
			for (var i = 0; i < n; i++)
				await tasks.UpsertAsync(Proj, board,
					[new NodePatch { Key = $"n{i}", Version = 0, Title = $"t{i}", Body = $"body number {i}" }]);
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
