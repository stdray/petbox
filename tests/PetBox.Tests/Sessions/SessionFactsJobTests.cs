using LinqToDB;
using Microsoft.Extensions.Logging;
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
// Shared per-class host (work share-fixtures-across-per-test-classes, wave 2): the migrated core +
// sessions + memory DB files are the expensive part of the constructor — the fixture owns the
// files, the test class rebuilds the (cheap) service graph per test. Per-test DATA isolation is
// TestDataReset.WipeAllTables over both per-project files — not TestDirs.ResetDbFile, which costs
// more than a fresh templated copy (see TestDataReset).
public sealed class SessionFactsJobFixture : IDisposable
{
	public const string Proj = "proj";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<SessionsDb> SessionsFactory { get; }
	public ScopedDbFactory<MemoryDb> MemoryFactory { get; }

	public SessionFactsJobFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessfacts-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		Db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		Db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		SessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), TestSchema.Sessions);
		MemoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
	}

	// Wipe both per-project files, plus the memory store CATALOG (MemoryStoreMeta lives in core,
	// like TaskBoards — MemoryStore.CreateAsync throws "already exists" against a leftover row).
	public void Reset()
	{
		Db.MemoryStores.Where(s => s.ProjectKey == Proj).Delete();
		using var sessions = SessionsFactory.NewEnsuredConnection(Proj);
		TestDataReset.WipeAllTables(sessions);
		using var memory = MemoryFactory.NewEnsuredConnection(Proj);
		TestDataReset.WipeAllTables(memory);
	}

	public void Dispose()
	{
		Db.Dispose();
		SessionsFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		MemoryFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}
}

public sealed class SessionFactsJobTests : IClassFixture<SessionFactsJobFixture>
{
	const string Proj = SessionFactsJobFixture.Proj;
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly SessionService _sessions;
	readonly MemoryService _memory;
	// A SECOND view of the same files, wired with an embedder. The plain `_memory` above is
	// lexical-only, and the lexical leg ANDs the query's tokens — fine for the tests that use a
	// candidate description copied verbatim from the seeded entry, useless for a REAL duplicate
	// pair, which is two different wordings of one fact and shares no full token set. Production
	// has an embedder, so the neighbour-sweep tests use this one and retrieval is honest.
	readonly MemoryService _semantic;
	readonly ScopedDbFactory<MemoryDb> _memoryFactory;

	public SessionFactsJobTests(SessionFactsJobFixture fx)
	{
		fx.Reset();
		_db = fx.Db;
		_sessionsFactory = fx.SessionsFactory;
		_memoryFactory = fx.MemoryFactory;
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		_memory = new MemoryService(new MemoryStore(_db.Factory(), fx.MemoryFactory), llm: null);
		_semantic = new MemoryService(new MemoryStore(_db.Factory(), fx.MemoryFactory), new BowEmbedder());
	}

	SessionFactsJob Job(ILlmClient? llm, TimeSpan? budget = null, ILogger<SessionFactsJob>? logger = null) =>
		new(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _memory, llm, logger,
			quietPeriod: NoQuiet, budget: budget);

	// Same job over the embedding-backed memory view. Seeding in those tests must go through
	// `_semantic` too, and the vectors must be driven in by hand — see RunSemanticAsync.
	SessionFactsJob SemanticJob(ILlmClient? llm, ILogger<SessionFactsJob>? logger = null) =>
		new(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _semantic, llm, logger,
			quietPeriod: NoQuiet);

	// Drive the neighbour-sweep tests. The vector index is CLASS-B: MemoryVectorizationJob writes
	// it on its own tick, not the upsert path — so a test that seeds entries and searches in the
	// same breath finds nothing semantically, while production has long since vectorized the
	// curated stores it is asking about. Running the real job here is what makes the retrieval in
	// these tests the same retrieval prod does, rather than a lexical stand-in that only matches
	// when the candidate repeats the neighbour's wording verbatim.
	async Task<int> RunSemanticAsync(ILlmClient chat, ILogger<SessionFactsJob>? logger = null)
	{
		await new MemoryVectorizationJob(_memoryFactory, new ProjectCatalog(_db.Factory()), new BowEmbedder())
			.DrainAllAsync(CancellationToken.None);
		return await SemanticJob(chat, logger).DrainAllAsync(CancellationToken.None);
	}

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
	public async Task Extracts_AcceptsWrappedFactsObject_TheNewCanonicalShape()
	{
		// json_object response_format forbids a bare top-level array on some upstreams, so the
		// canonical extraction shape going forward is {"facts":[...]} (facts-extraction-
		// unparseable-batches). This must parse exactly like the bare-array shape above.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("обсуждение", "итог: чинили парсер"));
		const string wrapped = """
			{"facts":[
			 {"type":"Feedback","description":"гоняй тесты с записью в лог","body":"повторный прогон ради скролла — расточительство","tags":"testing"},
			 {"type":"Project","description":"крокодиловый парсер падал на токене БУРУНДУК-42","body":"переполнение хвостового буфера; увеличен до 8 КБ"}
			]}
			""";
		var chat = new ScriptedChat(wrapped, """{"action":"add"}""");

		var captured = await Job(chat).DrainAllAsync(CancellationToken.None);

		captured.Should().Be(2);
		var entries = await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null);
		entries.Should().HaveCount(2);
		entries.Single(e => e.Type == "Project").Description.Should().Contain("БУРУНДУК-42");
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
	public async Task MalformedExtraction_RetriesOnceThenAdvancesCursor_NoCrash_NoRetryLoop()
	{
		// W: a genuinely unparseable answer (no JSON anywhere, real from the live log — the
		// model continued the transcript instead of processing it) gets exactly ONE corrective
		// retry, then gives up: warning logged, cursor advances, no repeat chat-burn on the
		// next pass.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		var chat = new ScriptedChat(
			"Два воркера батча 2 отчитались, третий ещё идёт. Прод пока на старом sha — деплой в процессе. Жду.");
		var logger = new CapturingLogger();
		var job = Job(chat, logger: logger);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(2); // extraction + the one corrective retry
		logger.Warnings.Should().ContainSingle(w =>
			w.Contains("unparseable", StringComparison.Ordinal) && w.Contains("after retry", StringComparison.Ordinal));

		var calls = chat.Calls;
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(calls); // the cursor moved past the bad batch — no chat burn loop
	}

	[Theory]
	[InlineData("[]\n\n---\n\n**Explanation:**  \nThe fragment is a deployment handoff between workers; nothing durable to record.")]
	[InlineData("[]\n\n<reasoning>\nThe transcript shows a routine status check with nothing worth extracting, so the array is empty.")]
	[InlineData("[]\n\n## Пояснение\n\nФрагмент содержит только служебный обмен статусами, извлекать нечего.")]
	public async Task EmptyArrayFollowedByProseOrUnclosedReasoning_ZeroCandidates_NoWarning(string raw)
	{
		// The dominant real-log shape: the model answered CORRECTLY ([] = no facts) and then
		// padded the answer with an explanation (verbatim forms from the live-log diagnosis,
		// including an UNCLOSED <reasoning> tag). This must recover to zero candidates WITHOUT
		// ever hitting the unparseable-batch warning path — the loss was never real.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		var chat = new ScriptedChat(raw);
		var logger = new CapturingLogger();

		(await Job(chat, logger: logger).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Calls.Should().Be(1); // recovered on the first attempt — no retry spent
		logger.Warnings.Should().BeEmpty();
	}

	[Fact]
	public async Task ProseThenValidJsonArray_CandidatesExtracted()
	{
		// Preamble BEFORE the JSON (not just after it) must also recover.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		const string raw = """
			Thinking about this fragment before answering.

			Here is the result: [{"type":"Project","description":"тестовый факт из прозы","body":"извлечён после преамбулы"}]
			""";
		var chat = new ScriptedChat(raw, """{"action":"add"}""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		var entries = await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null);
		entries.Should().ContainSingle(e => e.Description.Contains("тестовый факт из прозы"));
	}

	[Fact]
	public async Task FencedJsonAfterProse_CandidatesExtracted()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		const string raw = """
			Let me think about this fragment first.

			```json
			[{"type":"Feedback","description":"процедура из fenced блока","body":"извлечена после прозы","tags":"behavior:pattern"}]
			```

			Done.
			""";
		var chat = new ScriptedChat(raw, """{"action":"add"}""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		var entries = await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null);
		entries.Should().ContainSingle(e => e.Description.Contains("процедура из fenced блока"));
	}

	[Fact]
	public async Task JudgeVerdict_WithTrailingProse_ParsedNotSilentlySkipped()
	{
		// The judge parser shares ResilientJson with extraction: a `{...}` verdict followed by
		// explanatory prose must be parsed, not silently degraded to skip.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("обсуждение факта"));
		const string judgeRaw = """
			{"action":"add"}

			I'm adding this because it reflects a genuinely new, durable fact discovered this session.
			""";
		var chat = new ScriptedChat(
			"""[{"type":"Project","description":"новый факт с хвостатым вердиктом","body":"тело"}]""",
			judgeRaw);
		var logger = new CapturingLogger();

		(await Job(chat, logger: logger).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		chat.Calls.Should().Be(2); // extraction + judge, no retry needed
		logger.Warnings.Should().NotContain(w => w.Contains("judge", StringComparison.OrdinalIgnoreCase));
		(await _memory.ListAsync(Proj, SessionFactsJob.Store, type: null)).Should().ContainSingle();
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
	public async Task JudgeDrop_LogsMessageVersionRange()
	{
		// W3 (memory-telemetry-blind-paths pt.3): the drop-vardict warning must carry the same
		// [fromVersion, toVersion] shape a SAVED fact gets in metadata.messages — otherwise
		// "did this dropped fact come back later?" has no session range to check against, in
		// either direction. Two messages so fromVersion != toVersion — a fix that logs only one
		// of the two versions (or the same one twice) must still fail this.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs("реализовал фичу и задеплоил ci.512", "прогнал смок-тест, всё зелёное"));
		var (expectedFrom, expectedTo) = ((await _sessions.DeltaAsync(Proj, "s1", 0, CancellationToken.None))
			is [var first, .., var last] delta ? (first.Version, last.Version)
			: throw new InvalidOperationException("seeded session must have >=1 message"));
		var chat = new ScriptedChat(
			"""[{"type":"Project","description":"задеплоил ci.512 и прогнал смок","body":"нарратив о работе"}]""",
			"""{"action":"drop"}""");
		var logger = new CapturingLogger();

		(await Job(chat, logger: logger).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		var props = logger.WarningProperties.Should().ContainSingle().Subject;
		var messages = props.Should().ContainSingle(kv => kv.Key == "Messages").Subject.Value;
		messages.Should().BeEquivalentTo(new[] { expectedFrom, expectedTo }, o => o.WithStrictOrdering());
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

	// ---- the neighbour sweep is the project's stores, not a name literal -----------------------
	// (spec: autocapture-dedup; work `autocapture-dedup-blind-to-canon`; client report
	// kek-devices-classic-canon-498281 theme 3)
	//
	// The bug: CollectNeighborsAsync swept the literal { "notes", "autocaptured" }, so `canon` — and
	// any store a project curates under its own name — could not reach the judge's EXISTING block at
	// all. A repeat of curated knowledge was therefore not merely misjudged, it was UNJUDGEABLE.
	// These tests assert on the PROMPT, because that is where the regression lives: with a scripted
	// LLM the verdict is whatever the script says, but what the judge was ALLOWED TO SEE is real.

	// The real pair from the client report, verbatim (kek-devices, incident "channel 12"): a curated
	// canon entry and the autocaptured retelling of the same knowledge that was created anyway.
	const string CanonWifiDescription =
		"Дачный роутер после переезда стоял на канале 12 (2,4 ГГц) — устройства с US/world регуляторным доменом такую сеть вообще не видят";

	const string CanonWifiBody =
		"На роутере после переезда осталась домашняя настройка `channel 12` с шириной `40-below` " +
		"на 2,4 ГГц. **Каналы 12 и 13 не поддерживаются устройствами с американским или «мировым» " +
		"регуляторным доменом** — такое устройство сеть не видит вовсе, это выглядит как «сети нет», " +
		"а не «не подключается». Типично для дешёвых IoT-устройств, ТВ-приставок и части телефонов. " +
		"Исправлено: `no interface WifiMaster0 channel` (команда `channel auto` синтаксически неверна, " +
		"роутер отвечает `argument parse error`) — включает автовыбор, роутер встал на канал 1. " +
		"Ширина снижена до 20 МГц: на 2,4 ГГц 40 МГц почти всегда даёт больше помех, чем пользы.";

	const string CandidateWifiDescription =
		"Wi-Fi 2.4 GHz каналы 12/13 не видны устройствам с US/мировой регуляторной прошивкой";

	const string CandidateWifiJson =
		"""
		[{"type":"Project","description":"Wi-Fi 2.4 GHz каналы 12/13 не видны устройствам с US/мировой регуляторной прошивкой","body":"Роутер на даче вещал на канале 12 с шириной 40 МГц, из-за чего часть устройств не видела сеть. Переведено на автовыбор канала и ширину 20 МГц."}]
		""";

	async Task SeedCanonWifiAsync()
	{
		await _semantic.CreateStoreAsync(Proj, "canon", null);
		await _semantic.UpsertAsync(Proj, "canon", [new MemoryEntryInput
		{
			Key = "wifi-channel-12-invisible-to-devices", Version = 0, Type = "Feedback",
			Description = CanonWifiDescription, Body = CanonWifiBody,
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs("роутер на даче: канал 12 и ширина 40 МГц, устройства не видят сеть"));
	}

	[Fact]
	public async Task CanonEntry_ReachesTheJudge_AndItsSkipWritesNothing()
	{
		await SeedCanonWifiAsync();
		var chat = new RecordingChat(CandidateWifiJson, """{"action":"skip"}""");

		(await RunSemanticAsync(chat)).Should().Be(0);

		// 1) The curated entry was IN the prompt — under the old literal sweep it could not be.
		var prompt = chat.JudgePrompts.Should().ContainSingle().Subject;
		prompt.Should().Contain("\"canon\"");
		prompt.Should().Contain("wifi-channel-12-invisible-to-devices");
		// 2) …with the half that says what was DONE, which Clip(400) used to cut away, leaving the
		//    judge a symptom and no resolution.
		prompt.Should().Contain("no interface WifiMaster0 channel");
		prompt.Should().Contain("Ширина снижена до 20 МГц");
		// 3) A judge that CAN see it can rule "skip" — and the write path then adds nothing.
		(await _semantic.StoreExistsAsync(Proj, SessionFactsJob.Store)).Should().BeFalse();
	}

	[Fact]
	public async Task JudgeNamesCanonKey_ForUpdate_DegradesToAdd_AndSaysSoInTheLog()
	{
		// Curated stores are now visible to the judge, so it can NAME a canon key. The guarantee is
		// code, not prompt: `update` resolves the key inside the quarantine store only, misses, and
		// degrades to add — and (this card's acceptance point) now logs the miss the way `delete`
		// always has, instead of degrading silently.
		await SeedCanonWifiAsync();
		var before = (await _semantic.GetAsync(Proj, "canon", "wifi-channel-12-invisible-to-devices"))!.Version;
		var logger = new CapturingLogger();
		var chat = new RecordingChat(CandidateWifiJson,
			"""{"action":"update","key":"wifi-channel-12-invisible-to-devices","description":"x","body":"y"}""");

		(await RunSemanticAsync(chat, logger)).Should().Be(1);

		(await _semantic.GetAsync(Proj, "canon", "wifi-channel-12-invisible-to-devices"))!.Version
			.Should().Be(before);                                                    // curation untouched
		(await _semantic.ListAsync(Proj, SessionFactsJob.Store, type: null)).Should().HaveCount(1); // kept as add
		logger.Warnings.Should().ContainSingle(w =>
			w.Contains("UPDATE pointed at a non-quarantine", StringComparison.Ordinal)
			&& w.Contains("wifi-channel-12-invisible-to-devices", StringComparison.Ordinal));
	}

	[Fact]
	public async Task JudgeNamesCanonKey_ForDelete_IsIgnored_CurationSurvives()
	{
		await SeedCanonWifiAsync();
		var before = (await _semantic.GetAsync(Proj, "canon", "wifi-channel-12-invisible-to-devices"))!.Version;
		var chat = new RecordingChat(CandidateWifiJson,
			"""{"action":"delete","key":"wifi-channel-12-invisible-to-devices"}""");

		(await RunSemanticAsync(chat)).Should().Be(0);

		(await _semantic.GetAsync(Proj, "canon", "wifi-channel-12-invisible-to-devices"))!.Version
			.Should().Be(before); // a delete outside quarantine is never acted on
	}

	[Fact]
	public async Task CustomCuratedStore_ReachesTheJudge_ProvingItIsNotANameList()
	{
		// The card refuses "just add canon to the literal": a CLIENT's own curated store must be
		// visible too, and nobody will ever add its name to a set in our source. Sweeping the
		// catalog minus exclusions is what makes that free — this store is picked up with no
		// configuration anywhere.
		await _semantic.CreateStoreAsync(Proj, "dacha-notes", null);
		await _semantic.UpsertAsync(Proj, "dacha-notes", [new MemoryEntryInput
		{
			Key = "wifi-24-width", Version = 0, Type = "Project",
			Description = "на 2,4 ГГц ширина 20 МГц предпочтительнее 40 МГц", Body = "меньше помех",
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("ширина канала на 2,4 ГГц"));
		var chat = new RecordingChat(
			"""[{"type":"Project","description":"на 2,4 ГГц ширина 20 МГц предпочтительнее 40 МГц","body":"дубль"}]""",
			"""{"action":"skip"}""");

		await RunSemanticAsync(chat);

		var prompt = chat.JudgePrompts.Should().ContainSingle().Subject;
		prompt.Should().Contain("dacha-notes").And.Contain("wifi-24-width");
	}

	// ---- SECURITY: widening the sweep must not widen what leaves the box -----------------------
	[Fact]
	public async Task SensitiveStore_IsTheTopMatch_AndStillNeverReachesTheJudgePrompt()
	{
		// NON-NEGOTIABLE (card acceptance + MemoryStores contract): neighbour bodies are serialized
		// into a prompt for an EXTERNAL LLM. `ops` has held secrets and must never be auto-pulled.
		// The sweep is now "every store of the project", so `ops` is a store the sweep would reach
		// were the exclusion not there — which is exactly what makes this test worth having.
		await _semantic.CreateStoreAsync(Proj, "ops", null);
		await _semantic.UpsertAsync(Proj, "ops", [new MemoryEntryInput
		{
			Key = "router-admin", Version = 0, Type = "Reference",
			// Deliberately phrased to be the TOP lexical hit for the candidate below, so absence
			// from the prompt cannot be explained away as "the search just didn't rank it".
			Description = "роутер на даче канал 12 ширина 40 МГц пароль администратора",
			Body = "admin/hunter2-SEKRET на 192.168.1.1",
		}], []);
		await SeedCanonWifiAsync();
		var chat = new RecordingChat(CandidateWifiJson, """{"action":"skip"}""");

		await RunSemanticAsync(chat);

		chat.AllPrompts.Should().NotBeEmpty();
		var everythingSentToTheModel = string.Join("\n", chat.AllPrompts);
		everythingSentToTheModel.Should().NotContain("hunter2-SEKRET");
		everythingSentToTheModel.Should().NotContain("router-admin");
		everythingSentToTheModel.Should().NotContain("\"ops\"");
		// Control: the non-sensitive curated store DID make it, so the assertion above is about the
		// veto and not about a sweep that silently collected nothing.
		string.Join("\n", chat.JudgePrompts).Should().Contain("wifi-channel-12-invisible-to-devices");
	}

	[Fact]
	public async Task SessionDigestStore_IsExcluded_SoTheSourceIsNotCountedTwice()
	{
		// The OTHER leg of the exclusion set, and it is not about secrecy: `session-digests` holds
		// summaries of the very sessions this job distills, so sweeping it would let the source
		// corroborate itself. Excluded for double-counting — a digest is otherwise linkable.
		await _semantic.CreateStoreAsync(Proj, "session-digests", null);
		await _semantic.UpsertAsync(Proj, "session-digests", [new MemoryEntryInput
		{
			Key = "sd-1", Version = 0, Type = "Project",
			Description = "Wi-Fi 2.4 GHz каналы 12/13 не видны устройствам", Body = "дайджест сессии",
		}], []);
		await SeedCanonWifiAsync();
		var chat = new RecordingChat(CandidateWifiJson, """{"action":"skip"}""");

		await RunSemanticAsync(chat);

		var prompt = chat.JudgePrompts.Should().ContainSingle().Subject;
		prompt.Should().NotContain("session-digests").And.NotContain("sd-1");
		prompt.Should().Contain("wifi-channel-12-invisible-to-devices"); // control
	}

	[Fact]
	public async Task CuratedStore_KeepsItsSlot_WhenQuarantineIsFullOfFresherMatches()
	{
		// The trap this card warns about: widening the sweep changes the top-K distribution. A
		// GLOBAL top-K over the union would let a burst of fresh autocapture outrank the single
		// curated entry and push it out — making the fix a no-op exactly where it was needed.
		// Per-store K, interleaved by RANK, is what prevents that.
		await SeedCanonWifiAsync();
		await _semantic.CreateStoreAsync(Proj, SessionFactsJob.Store, null);
		await _semantic.UpsertAsync(Proj, SessionFactsJob.Store,
			Enumerable.Range(1, 10).Select(i => new MemoryEntryInput
			{
				Key = $"ac-noise{i}",
				Version = 0,
				Type = "Project",
				Description = $"Wi-Fi 2.4 GHz каналы 12/13 не видны устройствам, заметка {i}",
				Body = "свежий автозахват",
				Metadata = """{"sessionId":"s0"}""",
			}).ToList(), []);
		var chat = new RecordingChat(CandidateWifiJson, """{"action":"skip"}""");

		await RunSemanticAsync(chat);

		var prompt = chat.JudgePrompts.Should().ContainSingle().Subject;
		prompt.Should().Contain("wifi-channel-12-invisible-to-devices"); // curated survived the crowd
		prompt.Should().Contain("ac-noise");                             // quarantine still represented
	}

	[Fact]
	public async Task NeighborBodies_AreClippedToTheDeclaredBudget()
	{
		// The clip is a prompt-cost bound, so it must actually bind: one pathological curated entry
		// cannot spend the whole judge prompt. (The real canon entry of the incident is 629 chars
		// and now arrives WHOLE — asserted in CanonEntry_ReachesTheJudge above.)
		await _semantic.CreateStoreAsync(Proj, "canon", null);
		var huge = new string('ж', SessionFactsJob.NeighborBodyClip * 3);
		await _semantic.UpsertAsync(Proj, "canon", [new MemoryEntryInput
		{
			Key = "wall-of-text", Version = 0, Type = "Project",
			Description = CandidateWifiDescription, Body = huge,
		}], []);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("каналы 12 и 13 на 2,4 ГГц"));
		var chat = new RecordingChat(CandidateWifiJson, """{"action":"skip"}""");

		await RunSemanticAsync(chat);

		var prompt = chat.JudgePrompts.Should().ContainSingle().Subject;
		prompt.Should().Contain("wall-of-text");
		prompt.Should().Contain(new string('ж', SessionFactsJob.NeighborBodyClip));
		prompt.Should().NotContain(new string('ж', SessionFactsJob.NeighborBodyClip + 1));
	}

	// Captures every logged message by level so a test can assert a warning was (or, more often
	// here, was NOT) raised — the tolerant-parse recovery paths must stay silent at Warning.
	sealed class CapturingLogger : ILogger<SessionFactsJob>
	{
		public List<string> Warnings { get; } = [];
		// Structured properties per Warning call (same order as Warnings), for asserting on the
		// actual logged VALUES (e.g. the messages version-range array) rather than the rendered
		// text — mirrors how the real sink (SystemLogger) reads the templated state, not the
		// formatter's ToString().
		public List<IReadOnlyList<KeyValuePair<string, object?>>> WarningProperties { get; } = [];
		List<string> Infos { get; } = [];
		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
		public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
		public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
			Exception? exception, Func<TState, Exception?, string> formatter)
		{
			var message = formatter(state, exception);
			if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
			{
				Warnings.Add(message);
				if (state is IReadOnlyList<KeyValuePair<string, object?>> props) WarningProperties.Add(props);
			}
			else if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information) Infos.Add(message);
		}

		sealed class NullScope : IDisposable
		{
			public static readonly NullScope Instance = new();
			public void Dispose() { }
		}
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

	// Embed-only client for the MemoryService under the neighbour-sweep tests: deterministic
	// bag-of-words vectors, and Rerank reported UNAVAILABLE so the search pipeline stays on RRF
	// instead of reaching for a reranker this fake cannot serve.
	sealed class BowEmbedder : ILlmClient
	{
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException("the memory service never chats");

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(capability == LlmCapability.Embed);

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(HashedBagOfWords.Embed(request.Inputs));

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}

	// Scripted chat that RECORDS what it was asked, so a test can assert on the PROMPT rather than
	// on a verdict the script dictated anyway. `JudgePrompts` holds the user turn of every JUDGE
	// call (the CANDIDATE + EXISTING block); `AllPrompts` holds every turn of every call, system
	// messages included — the sweep for content that must never reach the model at all.
	sealed class RecordingChat(params string[] responses) : ILlmClient
	{
		readonly Queue<string> _queue = new(responses);
		string _last = responses[^1];
		public List<string> JudgePrompts { get; } = [];
		public List<string> AllPrompts { get; } = [];

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			var system = request.Messages[0].Content;
			AllPrompts.AddRange(request.Messages.Select(m => m.Content));
			if (!system.Contains("extract DURABLE", StringComparison.Ordinal))
				JudgePrompts.Add(string.Join("\n", request.Messages.Skip(1).Select(m => m.Content)));
			if (_queue.Count > 0) _last = _queue.Dequeue();
			return Task.FromResult(new ChatResult(_last, new ModelIdentity("fake-chat", 0),
				new ServedBy("fake", "fake-chat", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
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
