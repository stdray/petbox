using LinqToDB;
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
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_sessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);
		_memoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _memoryFactory), llm: null);
	}

	public void Dispose()
	{
		_db.Dispose();
		_sessionsFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_memoryFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	SessionFactsJob Job(ILlmClient? llm, TimeSpan? budget = null) =>
		new(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _memory, llm, logger: null,
			quietPeriod: NoQuiet, budget: budget);

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
		// The judge is ALWAYS consulted now (worth-gate): even with no existing neighbors each
		// candidate is judged — here it clears the gate with "add".
		var chat = new ScriptedChat(TwoFactsJson, """{"action":"add"}""");

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
		var chat = new ScriptedChat(TwoFactsJson, """{"action":"add"}""");
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
	public async Task ExactRepeat_JudgeSaysAdd_StructuralGuardSkips_NoDuplicate()
	{
		// The judge is a SOFT filter: even when it hallucinates "add" (or the neighbor search
		// never surfaced the twin), the deterministic guard behind it must catch an exact
		// repeat and write nothing.
		await _memory.CreateStoreAsync(Proj, SessionFactsJob.Store, null);
		await _memory.UpsertAsync(Proj, SessionFactsJob.Store, [new MemoryEntryInput
		{
			Key = "ac-known", Version = 0, Type = "Feedback",
			Description = "issue_task auto-close закрывает интейк issue на переходе work Done",
			Body = "уже знаем", Metadata = """{"sessionId":"s0"}""",
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("опять про issue_task auto-close"));
		var chat = new ScriptedChat(
			"""[{"type":"Feedback","description":"issue_task auto-close закрывает интейк issue на переходе work Done","body":"дубль"}]""",
			"""{"action":"add"}"""); // judge lets it through

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		(await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null)).Should().HaveCount(1); // guard held
	}

	[Fact]
	public async Task RephrasedRepeat_JudgeSaysAdd_SemanticGuardSkips_NoDuplicate()
	{
		await _memory.CreateStoreAsync(Proj, SessionFactsJob.Store, null);
		await _memory.UpsertAsync(Proj, SessionFactsJob.Store, [new MemoryEntryInput
		{
			Key = "ac-known", Version = 0, Type = "Feedback",
			Description = "issue_task auto-close закрывает интейк issue на переходе work Done",
			Body = "уже знаем", Metadata = """{"sessionId":"s0"}""",
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("снова issue_task auto-close, чуть иначе"));
		var chat = new EmbeddingChat(
			"""[{"type":"Feedback","description":"issue_task auto-close автоматически закрывает интейк issue на переходе work Done","body":"дубль иначе"}]""",
			"""{"action":"add"}"""); // judge misses the rephrase, guard's semantic leg catches it

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		(await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null)).Should().HaveCount(1);
	}

	[Fact]
	public async Task GenuinelyNewFact_IsWritten_GuardDoesNotOverSkip()
	{
		await _memory.CreateStoreAsync(Proj, SessionFactsJob.Store, null);
		await _memory.UpsertAsync(Proj, SessionFactsJob.Store, [new MemoryEntryInput
		{
			Key = "ac-known", Version = 0, Type = "Feedback",
			Description = "issue_task auto-close закрывает интейк issue на переходе work Done",
			Body = "уже знаем", Metadata = """{"sessionId":"s0"}""",
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("новое про gitversion и деплой"));
		var chat = new EmbeddingChat(
			"""[{"type":"Feedback","description":"gitversion падает на tag-only коммите нужен push main до move deploy","body":"новый факт"}]""",
			"""{"action":"add"}""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		(await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null)).Should().HaveCount(2); // distinct fact kept
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
		var chat = new ScriptedChat(TwoFactsJson, """{"action":"add"}""") { Available = false };
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Available = true;
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(2); // un-advanced cursor backfilled
	}

	[Fact]
	public async Task PerSessionCap_SpansAllBatches_NotPerBatch()
	{
		// W1: the per-session cap is HONEST across the whole DistillAsync pass. A long session
		// spans two extraction batches, each yielding 5 distinct facts (10 total). The old cap
		// was per-batch (Take(8) inside the loop) → up to 16; the new cap tops the SESSION at
		// MaxCandidatesPerSession no matter how many batches it took.
		var big = new string('ж', 4_000);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs(Enumerable.Range(1, 13).Select(i => $"{i}: {big}").ToArray())); // 12+1 → 2 batches
		var chat = new CapChat(perBatch: 5);

		var captured = await Job(chat).DrainAllAsync(CancellationToken.None);

		chat.ExtractCalls.Should().Be(2); // both batches were extracted (10 candidates offered)
		captured.Should().Be(SessionFactsJob.MaxCandidatesPerSession); // …but the session is capped
		(await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null))
			.Should().HaveCount(SessionFactsJob.MaxCandidatesPerSession);
	}

	[Fact]
	public async Task JudgeAlwaysConsulted_DropsNotWorthStoring_DeadLettered_NothingWritten()
	{
		// W1: with NO existing neighbors the judge is STILL consulted (worth-gate). Here it rules
		// the candidate not durable (narration) → "drop": nothing is written and no store is minted.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("реализовал фичу и задеплоил ci.512"));
		var chat = new ScriptedChat(
			"""[{"type":"Project","description":"задеплоил ci.512 и прогнал смок","body":"нарратив о работе"}]""",
			"""{"action":"drop"}""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Calls.Should().Be(2); // extraction + the judge WAS called despite zero neighbors
		(await _memory.StoreExistsAsync(Proj, SessionFactsJob.Store)).Should().BeFalse();
	}

	[Fact]
	public async Task JudgeDelete_InvalidatesStaleAutocapturedEntry()
	{
		// W2: the judge may invalidate a stale autocaptured entry — "delete" soft-removes it.
		await _memory.CreateStoreAsync(Proj, SessionFactsJob.Store, null);
		await _memory.UpsertAsync(Proj, SessionFactsJob.Store, [new MemoryEntryInput
		{
			Key = "ac-stale", Version = 0, Type = "Project",
			Description = "прод крутится на сервере tun3", Body = "устарело",
			Metadata = """{"sessionId":"s0"}""",
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("на самом деле прод давно на tun4, tun3 мёртв"));
		var chat = new ScriptedChat(
			"""[{"type":"Project","description":"прод переехал с tun3 на tun4","body":"tun3 больше не используется"}]""",
			"""{"action":"delete","key":"ac-stale"}""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		(await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null)).Should().BeEmpty(); // stale entry gone
	}

	[Fact]
	public async Task JudgeDelete_PointingAtCuratedNote_Ignored_NotesUntouched()
	{
		// W2 quarantine invariant: a "delete" that resolves to a NOTES key (or nowhere) is
		// ignored — the machine never removes human curation.
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes", [new MemoryEntryInput
		{
			Key = "curated", Version = 0, Type = "Project",
			Description = "куратор писал про tun3", Body = "рукописная заметка",
		}], []);
		var before = (await _memory.GetAsync(Proj, "notes", "curated"))!.Version;
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("прод переехал на tun4"));
		var chat = new ScriptedChat(
			"""[{"type":"Project","description":"прод переехал на tun4","body":"деталь"}]""",
			"""{"action":"delete","key":"curated"}"""); // judge misfires at a curated key

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		(await _memory.GetAsync(Proj, "notes", "curated"))!.Version.Should().Be(before);      // untouched
		(await _memory.StoreExistsAsync(Proj, SessionFactsJob.Store)).Should().BeFalse();     // no quarantine write
	}

	[Fact]
	public async Task DedupGuard_EmbedsStoreOncePerPass_NotPerCandidate()
	{
		// W2: the embed cache. Two distinct new candidates in one pass are each deduped against
		// the pre-seeded store entry; its text must be embedded ONCE for the whole pass (it used
		// to be re-embedded on every candidate).
		const string seed = "issue_task auto-close закрывает интейк issue на переходе work Done";
		await _memory.CreateStoreAsync(Proj, SessionFactsJob.Store, null);
		await _memory.UpsertAsync(Proj, SessionFactsJob.Store, [new MemoryEntryInput
		{
			Key = "ac-known", Version = 0, Type = "Feedback", Description = seed, Body = "уже знаем",
			Metadata = """{"sessionId":"s0"}""",
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("две разные новые темы"));
		var chat = new CountingEmbedChat(
			"""
			[{"type":"Feedback","description":"gitversion падает на tag-only коммите нужен push main","body":"ф1"},
			 {"type":"Reference","description":"worktree wwwroot пуст без bun install и build","body":"ф2"}]
			""",
			"""{"action":"add"}""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(2); // both distinct facts written

		chat.EmbedInputs.Count(t => t == seed).Should().Be(1); // store text embedded once, cache reused it
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

	// Scripted chat that ALSO embeds (deterministic bag-of-words → exact BoW cosine), so the
	// dedup guard's semantic leg is exercised: a rephrasing that shares (nearly) all tokens
	// clears the threshold, a distinct fact stays well below it.
	sealed class EmbeddingChat(params string[] responses) : ILlmClient
	{
		readonly Queue<string> _queue = new(responses);
		string _last = responses[^1];

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			if (_queue.Count > 0) _last = _queue.Dequeue();
			return Task.FromResult(new ChatResult(_last, new ModelIdentity("fake-chat", 0),
				new ServedBy("fake", "fake-chat", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(HashedBagOfWords.Embed(request.Inputs));

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}

	// Role-aware chat: every EXTRACT call returns `perBatch` fresh DISTINCT facts (namespaced by
	// the extract-call index so batches never collide), every JUDGE call returns "add". Embedding
	// is unsupported → the dedup guard degrades to text-only, and the distinct descriptions never
	// collide there either. Lets a multi-batch session offer more candidates than the cap.
	sealed class CapChat(int perBatch) : ILlmClient
	{
		public int ExtractCalls { get; private set; }

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			var system = request.Messages[0].Content;
			string resp;
			if (system.Contains("extract DURABLE", StringComparison.Ordinal))
			{
				ExtractCalls++;
				var items = Enumerable.Range(1, perBatch).Select(i =>
					$$"""{"type":"Project","description":"уникальный факт {{ExtractCalls}} {{i}}","body":"тело {{ExtractCalls}} {{i}}"}""");
				resp = "[" + string.Join(",", items) + "]";
			}
			else resp = """{"action":"add"}""";
			return Task.FromResult(new ChatResult(resp, new ModelIdentity("fake-chat", 0),
				new ServedBy("fake", "fake-chat", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}

	// Scripted chat that embeds (BoW) and RECORDS every text handed to EmbedAsync, so a test can
	// assert the per-pass cache embedded each store text only once.
	sealed class CountingEmbedChat(params string[] responses) : ILlmClient
	{
		readonly Queue<string> _queue = new(responses);
		string _last = responses[^1];
		public List<string> EmbedInputs { get; } = [];

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			if (_queue.Count > 0) _last = _queue.Dequeue();
			return Task.FromResult(new ChatResult(_last, new ModelIdentity("fake-chat", 0),
				new ServedBy("fake", "fake-chat", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default)
		{
			EmbedInputs.AddRange(request.Inputs);
			return Task.FromResult(HashedBagOfWords.Embed(request.Inputs));
		}

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}
}

// Deterministic, BATCH-INDEPENDENT bag-of-words embedder: each token maps to a FIXED dimension
// (FNV-1a hash mod dim), so a given text always embeds to the same vector regardless of which
// batch it rode in. That is what a real embedder does, and it is what makes the per-pass
// embedding cache transparent — a per-call vocab would give a cached text a different basis.
static class HashedBagOfWords
{
	const int Dim = 1024;

	public static EmbedResult Embed(IReadOnlyList<string> inputs)
	{
		var vectors = inputs.Select(Vector).ToList();
		return new EmbedResult(vectors, new ModelIdentity("fake-embed", 0),
			new ServedBy("fake", "fake-embed", 1, Degraded: false));
	}

	static float[] Vector(string? text)
	{
		var v = new float[Dim];
		foreach (var tok in Tokenize(text))
		{
			uint h = 2166136261;
			foreach (var c in tok) h = (h ^ c) * 16777619;
			v[h % Dim] += 1f;
		}
		return v;
	}

	static IEnumerable<string> Tokenize(string? s) =>
		(s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.Select(t => new string(t.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()))
			.Where(t => t.Length > 0);
}
