using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Services;
using PetBox.Web.Search;

namespace PetBox.Tests.Sessions;

// The autocapture promises (spec: memory-autocapture + dedup/quarantine/provenance):
// durable facts distill out of settled sessions into the QUARANTINED `autocaptured`
// store with verbatim provenance; repeats are judged against retrieved neighbors and
// never duplicate; curated stores are never machine-modified; bad LLM output neither
// crashes the pass nor burns chat calls forever.
[Collection("DataModule")]
public sealed class SessionFactsJobTests : IDisposable
{
	const string Proj = "proj";
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly ScopedDbFactory<MemoryDb> _memoryFactory;
	readonly SessionService _sessions;
	readonly MemoryService _memory;

	public SessionFactsJobTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessfacts-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_sessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);
		_memoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		_memory = new MemoryService(new MemoryStore(_db, _memoryFactory), llm: null);
	}

	public void Dispose()
	{
		_db.Dispose();
		_sessionsFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_memoryFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	SessionFactsJob Job(ILlmClient? llm, TimeSpan? budget = null) =>
		new(_sessionsFactory, _sessions, _memory, llm, logger: null, quietPeriod: NoQuiet, budget: budget);

	[Fact]
	public async Task MultiBatchBacklog_DrainsFullyInOnePass()
	{
		// Content caps at 4k before batching → 12 messages per batch; 13 messages =
		// 2 extraction batches; previously hard-capped at one batch per pass.
		var big = new string('ж', 4_000);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs(Enumerable.Range(1, 13).Select(i => $"{i}: {big}").ToArray()));
		var chat = new ScriptedChat("[]");
		var job = Job(chat);

		await job.DrainAllAsync(CancellationToken.None);
		chat.Calls.Should().Be(2); // both batches extracted this pass

		await job.DrainAllAsync(CancellationToken.None);
		chat.Calls.Should().Be(2); // cursor at the end — nothing left
	}

	static SessionMessageInput[] Msgs(params string[] contents) =>
		contents.Select(c => new SessionMessageInput("user", c)).ToArray();

	const string TwoFactsJson =
		"""
		[
		 {"type":"Feedback","description":"гоняй тесты с записью в лог","body":"повторный прогон ради скролла — расточительство","tags":"testing"},
		 {"type":"Project","description":"крокодиловый парсер падал на токене БУРУНДУК-42","body":"переполнение хвостового буфера; увеличен до 8 КБ"}
		]
		""";

	[Fact]
	public async Task Extracts_WritesQuarantinedFacts_WithProvenance()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("обсуждение", "итог: чинили парсер"));
		var chat = new ScriptedChat(TwoFactsJson); // no memory stores exist yet → no judge calls

		var captured = await Job(chat).DrainAllAsync(CancellationToken.None);

		captured.Should().Be(2);
		var entries = await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null);
		entries.Should().HaveCount(2);
		var fact = entries.Single(e => e.Type == "Project");
		fact.Description.Should().Contain("БУРУНДУК-42");
		fact.Tags.Should().Contain(SessionFactsJob.Tag);
		fact.Metadata.Should().Contain("\"sessionId\":\"s1\"").And.Contain("[1,2]"); // the verbatim bridge
		entries.Single(e => e.Type == "Feedback").Tags.Should().Contain("testing");
	}

	[Fact]
	public async Task SecondPass_NoNewMessages_NoChatSpent()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		var chat = new ScriptedChat(TwoFactsJson);
		var job = Job(chat);
		await job.DrainAllAsync(CancellationToken.None);
		var calls = chat.Calls;

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(calls); // cursor held — nothing re-distilled
	}

	[Fact]
	public async Task DuplicateOfCuratedNote_JudgeSkips_NothingWritten_NotesUntouched()
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes", [new MemoryEntryInput
		{
			Key = "known", Version = 0, Type = "Project",
			Description = "парсер падал на БУРУНДУК-42", Body = "уже знаем",
		}], []);
		var before = (await _memory.GetAsync(Proj, "notes", "known"))!.Version;
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("опять про парсер и БУРУНДУК-42"));
		var chat = new ScriptedChat(
			"""[{"type":"Project","description":"парсер падал на БУРУНДУК-42","body":"дубль"}]""",
			"""{"action":"skip"}""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		(await _memory.StoreExistsAsync(Proj, SessionFactsJob.Store)).Should().BeFalse(); // no quarantine entry
		(await _memory.GetAsync(Proj, "notes", "known"))!.Version.Should().Be(before);    // curation untouched
	}

	[Fact]
	public async Task JudgeUpdate_MergesIntoExistingAutocapturedEntry()
	{
		await _memory.CreateStoreAsync(Proj, SessionFactsJob.Store, null);
		await _memory.UpsertAsync(Proj, SessionFactsJob.Store, [new MemoryEntryInput
		{
			Key = "ac-seed", Version = 0, Type = "Project",
			Description = "парсер падал на БУРУНДУК-42", Body = "первая версия",
			Metadata = """{"sessionId":"s0"}""",
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("новая деталь про БУРУНДУК-42: буфер 8 КБ"));
		var chat = new ScriptedChat(
			"""[{"type":"Project","description":"парсер падал на БУРУНДУК-42","body":"и буфер 8 КБ"}]""",
			"""{"action":"update","key":"ac-seed","description":"парсер падал на БУРУНДУК-42","body":"первая версия + буфер увеличен до 8 КБ"}""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		var entries = await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null);
		entries.Should().HaveCount(1); // merged, not duplicated
		entries[0].Body.Should().Contain("8 КБ");
		entries[0].Metadata.Should().Contain("\"sessionId\":\"s1\""); // newest source up front…
		entries[0].Metadata.Should().Contain("seenIn").And.Contain("s0"); // …prior provenance accumulated, not erased
	}

	[Fact]
	public async Task JudgePointsAtCuratedKey_DegradesToAdd_NotesNeverModified()
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes", [new MemoryEntryInput
		{
			Key = "known", Version = 0, Type = "Project",
			Description = "парсер падал на БУРУНДУК-42", Body = "куратор писал",
		}], []);
		var before = (await _memory.GetAsync(Proj, "notes", "known"))!.Version;
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("деталь про БУРУНДУК-42"));
		var chat = new ScriptedChat(
			"""[{"type":"Project","description":"деталь про БУРУНДУК-42","body":"новая деталь"}]""",
			"""{"action":"update","key":"known","description":"x","body":"y"}"""); // judge misbehaves

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		(await _memory.GetAsync(Proj, "notes", "known"))!.Version.Should().Be(before); // quarantine invariant
		(await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null)).Should().HaveCount(1); // knowledge kept as add
	}

	[Fact]
	public async Task MalformedExtraction_AdvancesCursor_NoCrash_NoRetryLoop()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		var chat = new ScriptedChat("это не json");
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		var calls = chat.Calls;
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(calls); // the cursor moved past the bad batch — no chat burn loop
	}

	[Fact]
	public async Task ChatUnavailable_NoOp_ThenBackfillsOnRecovery()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		var chat = new ScriptedChat(TwoFactsJson) { Available = false };
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Available = true;
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(2); // un-advanced cursor backfilled
	}

	// Chat fake answering from a scripted queue (extraction first, then judge calls); the
	// last response repeats when the queue runs dry.
	sealed class ScriptedChat(params string[] responses) : ILlmClient
	{
		readonly Queue<string> _queue = new(responses);
		string _last = responses[^1];
		public int Calls { get; private set; }
		public bool Available { get; set; } = true;

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			Calls++;
			if (_queue.Count > 0) _last = _queue.Dequeue();
			return Task.FromResult(new ChatResult(_last, new ModelIdentity("fake-chat", 0),
				new ServedBy("fake", "fake-chat", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(Available);
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}
}
