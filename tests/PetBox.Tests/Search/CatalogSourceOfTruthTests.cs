using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Search;
using PetBox.Sessions.Services;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tests.Memory;
using PetBox.Web.Search;

namespace PetBox.Tests.Search;

// spec: catalog-is-source-of-truth — the background enrichment jobs must learn WHICH PROJECTS EXIST
// from the core-db catalog, never from `{tier}/*.db` on disk. Per-project SQLite files are created
// LAZILY (first write), so a file scan is wrong in both directions and every job is pinned on both:
//
//   NO FILE YET — a project the catalog knows about but whose per-project file has not been
//     materialized. The file scan never saw it, so the job skipped it SILENTLY, forever. The pass
//     must now visit it; visiting means opening the store, which materializes the file with its
//     migrations run (deliberate — see each job's header). "The file now exists" is therefore the
//     observable proof that the project was no longer skipped.
//
//   GHOST — a DELETED project whose file still sits on disk (ProjectDeletion cascades the catalog
//     rows; the orphan-cleanup services reclaim the files on a later tick). The file scan kept
//     working it: burning LLM calls, and — for the jobs that WRITE — resurrecting files the sweeper
//     had just reclaimed. The pass must not touch it.
public sealed class CatalogSourceOfTruthTests : IDisposable
{
	// In the catalog, no per-project db file anywhere.
	const string Fresh = "fresh";
	// Files on disk, no catalog rows (a deleted project, sweeper not run yet).
	const string Ghost = "ghost";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ProjectCatalog _catalog;
	readonly ScopedDbFactory<MemoryDb> _memoryFactory;
	readonly ScopedDbFactory<TasksDb> _tasksFactory;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly MemoryService _memory;
	readonly SessionService _sessions;
	readonly TaskBoardStore _boards;
	readonly TasksService _tasks;

	public CatalogSourceOfTruthTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-catalogsot-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_catalog = new ProjectCatalog(_db);

		_memoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_tasksFactory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_sessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);

		_memory = new MemoryService(new MemoryStore(_db, _memoryFactory), llm: null);
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		_boards = new TaskBoardStore(_db, _tasksFactory);
		_tasks = new TasksService(_boards, new RelationStore(_tasksFactory), new TagStore(_tasksFactory),
			new CommentService(_tasksFactory), llm: null);

		_db.Insert(new Project { Key = Fresh, WorkspaceKey = "ws", Name = "Fresh", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		_memoryFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_tasksFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_sessionsFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// ---- fixtures ----

	string MemoryFile(string project) => Path.Combine(_dir, "memory", project + ".db");
	string TasksFile(string project) => Path.Combine(_dir, "tasks", project + ".db");
	string SessionsFile(string project) => Path.Combine(_dir, "sessions", project + ".db");

	// A catalog row WITHOUT the per-project file: exactly the lazy-creation window the file scan was
	// blind to (the row is written to core.db; the file appears only on the first write to it).
	// Inserted directly, so no service call materializes the file behind our back.
	void CatalogMemoryStore(string project, string store) =>
		_db.Insert(new MemoryStoreMeta
		{
			ProjectKey = project,
			Name = store,
			CreatedAt = DateTime.UtcNow,
			UpdatedAt = DateTime.UtcNow,
			IsSystem = MemoryStore.SystemStoreNames.Contains(store),
		});

	void CatalogTaskBoard(string project, string board) =>
		_db.Insert(new TaskBoardMeta
		{
			ProjectKey = project,
			Name = board,
			Kind = "simple",
			CreatedAt = DateTime.UtcNow,
			UpdatedAt = DateTime.UtcNow,
		});

	// Seeds real content for `Ghost` through the normal service paths (which create the files AND the
	// catalog rows), then DELETES the project exactly as ProjectDeletion does — cascading every
	// catalog row away and leaving the files behind. That is a production ghost, byte for byte.
	async Task SeedGhostAsync(bool memory = false, bool tasks = false, bool sessions = false,
		string memoryStore = SessionFactsJob.Store)
	{
		await _db.InsertAsync(new Project { Key = Ghost, WorkspaceKey = "ws", Name = "G", Description = "" });

		if (memory)
			await _memory.UpsertAsync(Ghost, memoryStore,
			[
				new MemoryEntryInput { Key = "g1", Version = 0, Type = "Feedback", Description = "когда А — делай Б", Body = "наблюдение из s-1", Metadata = "{\"sessionId\":\"s-1\"}" },
				new MemoryEntryInput { Key = "g2", Version = 0, Type = "Feedback", Description = "когда В — делай Г", Body = "наблюдение из s-2", Metadata = "{\"sessionId\":\"s-2\"}" },
			], []);

		if (tasks)
		{
			await _tasks.CreateBoardAsync(Ghost, "b", "simple", null, null);
			await _tasks.UpsertAsync(Ghost, "b",
				[new NodePatch { Key = "n1", Version = 0, Title = "ghost node", Body = "ghost body text" }]);
		}

		if (sessions)
			await _sessions.UpsertAsync(Ghost, "s-ghost", "claude-code",
				[new SessionMessageInput("user", "мы чинили падение резолвера конфигов и починили его")]);

		var deleted = await ProjectDeletion.DeleteAsync(_db, Ghost);
		deleted.Should().BeTrue();
	}

	// The per-project file's own rows — read raw so the assertion never routes through a service that
	// would (re)create the file it is meant to be inspecting.
	static int Count(string file, string sql)
	{
		using var db = new MemoryDb(MemoryDb.CreateOptions($"Data Source={file}"));
		return db.Execute<int>(sql);
	}

	// ---- MemoryVectorizationJob ----

	MemoryVectorizationJob MemVecJob() => new(_memoryFactory, _catalog, new FakeLlmClient());

	[Fact]
	public async Task MemoryVectorization_sees_a_project_whose_db_file_does_not_exist_yet()
	{
		CatalogMemoryStore(Fresh, "notes");
		File.Exists(MemoryFile(Fresh)).Should().BeFalse("the store is a catalog row; the file is lazy");

		await MemVecJob().DrainAllAsync(CancellationToken.None);

		// The pass VISITED the project — a file scan would have had nothing to iterate at all.
		File.Exists(MemoryFile(Fresh)).Should().BeTrue("the catalog says this project has memory");
	}

	[Fact]
	public async Task MemoryVectorization_ignores_a_ghost_file_whose_project_is_gone()
	{
		await SeedGhostAsync(memory: true);
		File.Exists(MemoryFile(Ghost)).Should().BeTrue("the orphan sweeper has not run yet");

		await MemVecJob().DrainAllAsync(CancellationToken.None);

		// The file scan drained the ghost — vectors + a cursor row. The catalog does not know it.
		Count(MemoryFile(Ghost), "SELECT COUNT(*) FROM search_vec").Should().Be(0);
		Count(MemoryFile(Ghost), "SELECT COUNT(*) FROM search_cursor").Should().Be(0);
	}

	// ---- TasksVectorizationJob ----

	TasksVectorizationJob TaskVecJob() => new(_tasksFactory, _catalog, new FakeLlmClient());

	[Fact]
	public async Task TasksVectorization_sees_a_project_whose_db_file_does_not_exist_yet()
	{
		CatalogTaskBoard(Fresh, "b");
		File.Exists(TasksFile(Fresh)).Should().BeFalse("the board is a catalog row; the file is lazy");

		await TaskVecJob().DrainAllAsync(CancellationToken.None);

		File.Exists(TasksFile(Fresh)).Should().BeTrue("the catalog says this project has a board");
	}

	[Fact]
	public async Task TasksVectorization_ignores_a_ghost_file_whose_project_is_gone()
	{
		await SeedGhostAsync(tasks: true);
		File.Exists(TasksFile(Ghost)).Should().BeTrue();

		await TaskVecJob().DrainAllAsync(CancellationToken.None);

		Count(TasksFile(Ghost), "SELECT COUNT(*) FROM search_vec").Should().Be(0);
		Count(TasksFile(Ghost), "SELECT COUNT(*) FROM search_cursor").Should().Be(0);
	}

	// ---- SessionTermIndex (chat-free; the one job with no gate before the store) ----

	SessionTermIndex TermIndex() => new(_sessionsFactory, _catalog, _sessions);

	[Fact]
	public async Task SessionTermIndex_sees_a_project_whose_db_file_does_not_exist_yet()
	{
		File.Exists(SessionsFile(Fresh)).Should().BeFalse();

		await TermIndex().DrainAllAsync(CancellationToken.None);

		// Deliberate: sessions have no per-entity catalog, so the project catalog IS the work list —
		// the pass opens (and thereby migrates) the file of every project it is told exists.
		File.Exists(SessionsFile(Fresh)).Should().BeTrue();
	}

	[Fact]
	public async Task SessionTermIndex_ignores_a_ghost_file_whose_project_is_gone()
	{
		await SeedGhostAsync(sessions: true);

		var indexed = await TermIndex().DrainAllAsync(CancellationToken.None);

		indexed.Should().Be(0, "the only session on disk belongs to a project that no longer exists");
		Count(SessionsFile(Ghost), "SELECT COUNT(*) FROM session_term_fts").Should().Be(0);
	}

	// ---- SessionDigestJob ----

	SessionDigestJob DigestJob() =>
		new(_catalog, _sessions, _memory, new StubChat("Сессия про фикс\n- фикс резолвера"),
			logger: null, quietPeriod: TimeSpan.FromMinutes(-5));

	[Fact]
	public async Task SessionDigest_sees_a_project_whose_db_file_does_not_exist_yet()
	{
		File.Exists(SessionsFile(Fresh)).Should().BeFalse();

		await DigestJob().DrainAllAsync(CancellationToken.None);

		File.Exists(SessionsFile(Fresh)).Should().BeTrue("the catalog lists the project, so the pass visits it");
	}

	[Fact]
	public async Task SessionDigest_ignores_a_ghost_file_whose_project_is_gone()
	{
		await SeedGhostAsync(sessions: true);

		var distilled = await DigestJob().DrainAllAsync(CancellationToken.None);

		distilled.Should().Be(0);
		// The file scan distilled the ghost's session and wrote the digest into the ghost's MEMORY
		// file — re-creating a file the orphan sweeper is trying to reclaim. Nothing may be written.
		File.Exists(MemoryFile(Ghost)).Should().BeFalse();
	}

	// ---- SessionFactsJob ----

	SessionFactsJob FactsJob() =>
		new(_sessionsFactory, _catalog, _sessions, _memory, new StubChat("[]"),
			logger: null, quietPeriod: TimeSpan.FromMinutes(-5));

	[Fact]
	public async Task SessionFacts_sees_a_project_whose_db_file_does_not_exist_yet()
	{
		File.Exists(SessionsFile(Fresh)).Should().BeFalse();

		await FactsJob().DrainAllAsync(CancellationToken.None);

		File.Exists(SessionsFile(Fresh)).Should().BeTrue();
	}

	[Fact]
	public async Task SessionFacts_ignores_a_ghost_file_whose_project_is_gone()
	{
		await SeedGhostAsync(sessions: true);

		var captured = await FactsJob().DrainAllAsync(CancellationToken.None);

		captured.Should().Be(0);
		File.Exists(MemoryFile(Ghost)).Should().BeFalse("autocapture must not write into a deleted project");
	}

	// ---- BehaviorPatternJob ----

	BehaviorPatternJob PatternJob() =>
		new(_memoryFactory, _catalog, _memory, new StubChat(PatternJson));

	// One consolidated pattern over two distinct sources — what the miner would write if it ran.
	const string PatternJson =
		"""[{"description":"когда А — делай Б","body":"detail","sources":["s-1","s-2"]}]""";

	[Fact]
	public async Task BehaviorPattern_sees_a_project_whose_db_file_does_not_exist_yet()
	{
		CatalogMemoryStore(Fresh, SessionFactsJob.Store);
		File.Exists(MemoryFile(Fresh)).Should().BeFalse();

		await PatternJob().DrainAllAsync(CancellationToken.None);

		// Visited: the quarantine store is in the catalog, so the pass opened the (lazy) file to read
		// its mining cursor. Nothing is mined — the store is empty — but the project is no longer invisible.
		File.Exists(MemoryFile(Fresh)).Should().BeTrue();
	}

	[Fact]
	public async Task BehaviorPattern_ignores_a_ghost_file_whose_project_is_gone()
	{
		await SeedGhostAsync(memory: true); // two Feedback observations, two distinct sessionIds

		var mined = await PatternJob().DrainAllAsync(CancellationToken.None);

		mined.Should().Be(0);
		// Not one `bp-…` row: the miner is a WRITER, so a ghost pass resurrects a reclaimed file.
		Count(MemoryFile(Ghost), "SELECT COUNT(*) FROM memory_entries WHERE Key LIKE 'bp-%'").Should().Be(0);
	}

	// ---- MemoryQuarantineGcJob ----

	MemoryQuarantineGcJob GcJob() =>
		new(_catalog, _memory, logger: null, minAge: TimeSpan.FromMinutes(-5), enforce: true,
			scanInterval: TimeSpan.Zero);

	[Fact]
	public async Task QuarantineGc_sees_a_project_whose_db_file_does_not_exist_yet()
	{
		CatalogMemoryStore(Fresh, MemoryQuarantineGcJob.Store);
		File.Exists(MemoryFile(Fresh)).Should().BeFalse();

		await GcJob().DrainAllAsync(CancellationToken.None);

		File.Exists(MemoryFile(Fresh)).Should().BeTrue();
	}

	[Fact]
	public async Task QuarantineGc_ignores_a_ghost_file_whose_project_is_gone()
	{
		await SeedGhostAsync(memory: true, memoryStore: MemoryQuarantineGcJob.Store);

		var retired = await GcJob().DrainAllAsync(CancellationToken.None);

		retired.Should().Be(0);
		// Under enforce the file scan soft-deleted the ghost's aged entries — writing to a file whose
		// project is gone. Both entries must still be active.
		Count(MemoryFile(Ghost), "SELECT COUNT(*) FROM memory_entries WHERE ActiveTo IS NULL").Should().Be(2);
	}

	// ---- catalog contract ----

	[Fact]
	public async Task Catalog_lists_the_project_and_the_reserved_builtins_but_never_the_ghost()
	{
		await SeedGhostAsync(memory: true, tasks: true, sessions: true);

		var projects = await _catalog.ListProjectKeysAsync();
		projects.Should().Contain(Fresh).And.NotContain(Ghost);
		// The reserved built-ins are REAL Projects rows (seeded by the core schema; "$ws-*" is added
		// by WorkspaceMemory.EnsureContainerAsync). The file scan they replace saw their memory files,
		// so the catalog list must keep seeing them — otherwise canon/shared memory silently stops
		// being enriched.
		projects.Should().Contain(["$system", WorkspaceMemory.SystemContainer]);

		// Deleting the project cascaded its per-entity catalog rows away, so the tiers' own catalogs
		// no longer name it — the files on disk are all that is left, and they are not the truth.
		(await _catalog.ListMemoryProjectKeysAsync()).Should().NotContain(Ghost);
		(await _catalog.ListTaskProjectKeysAsync()).Should().NotContain(Ghost);
	}

	// Chat that answers with one fixed text and is always available; embedding is deterministic
	// (the pattern miner's dedup guard embeds), rerank is unused.
	sealed class StubChat(string text) : ILlmClient
	{
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			Task.FromResult(new ChatResult(text, new ModelIdentity("fake-chat", 0),
				new ServedBy("fake", "fake-chat", 1, Degraded: false)));

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(new EmbedResult(
				request.Inputs.Select(i => { var v = new float[8]; v[Math.Abs(i.GetHashCode()) % 8] = 1f; return v; }).ToList(),
				new ModelIdentity("fake-embed-v1", 8), new ServedBy("fake", "fake-embed-v1", 1, Degraded: false)));

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}
}
