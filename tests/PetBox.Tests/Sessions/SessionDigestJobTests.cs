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

// The discovery tier of session search: the digest job distills a session's transcript
// into the project's `session-digests` memory store, incrementally (the digest entry's
// metadata cursor feeds ISessionService.DeltaAsync), without ever re-distilling an
// unchanged session, and degrades to a no-op when chat is unavailable.
public sealed class SessionDigestJobTests : IDisposable
{
	const string Proj = "proj";
	// No quiet period in tests: a just-written session is immediately eligible.
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly ScopedDbFactory<MemoryDb> _memoryFactory;
	readonly SessionService _sessions;
	readonly MemoryService _memory;

	public SessionDigestJobTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessdigest-" + Guid.NewGuid().ToString("N"));
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

	SessionDigestJob Job(ILlmClient? llm, TimeSpan? quiet = null, TimeSpan? budget = null,
		ILogger<SessionDigestJob>? logger = null) =>
		new(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _memory, llm, logger: logger,
			quietPeriod: quiet ?? NoQuiet, budget: budget);

	// A single message that clears the MinDistillChars (40) substance floor, so mechanics
	// tests (cursor/delta/availability) actually reach the distiller — they are not testing
	// the empty-session gate, which the dedicated tests below cover.
	const string Big1 = "we investigated the flaky config resolver test and reproduced it locally";
	const string Big2 = "then we shipped the fix to ci.512 and confirmed the smoke run passed";

	static SessionMessageInput[] Msgs(params string[] contents) =>
		contents.Select(c => new SessionMessageInput("user", c)).ToArray();

	static long Cursor(string metadata)
	{
		using var doc = System.Text.Json.JsonDocument.Parse(metadata);
		return doc.RootElement.GetProperty("cursor").GetInt64();
	}

	[Fact]
	public async Task Distill_NewSession_WritesDigestEntryWithCursor()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("fixed the NRE in ConfigResolver", "deployed ci.300"));
		var chat = new ChatFake { NextText = "Сессия про фикс NRE в ConfigResolver\n- fixed NRE\n- deployed ci.300" };

		var distilled = await Job(chat).DrainAllAsync(CancellationToken.None);

		distilled.Should().Be(1);
		var entry = await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1");
		entry.Should().NotBeNull();
		entry!.Type.Should().Be("Reference");
		entry.Description.Should().Be("Сессия про фикс NRE в ConfigResolver");
		entry.Body.Should().Contain("- fixed NRE").And.Contain("- deployed ci.300");
		entry.Tags.Should().Contain(SessionDigestJob.Tag);
		Cursor(entry.Metadata).Should().Be(2); // last distilled message ordinal
	}

	[Fact]
	public async Task SecondPass_NoNewMessages_IsNoOpWithoutChatCalls()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1, Big2));
		var chat = new ChatFake();
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(1);
		var calls = chat.Prompts.Count;

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Prompts.Count.Should().Be(calls); // cursor held the line — no chat spent
	}

	[Fact]
	public async Task Delta_RedistillsOnlyNewMessages_AndMergesIntoExistingDigest()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs("old-alpha: initial pass over the config resolver null-reference bug",
				"old-beta: reproduced the crash locally against the staging snapshot"));
		var chat = new ChatFake { NextText = "digest v1\n- old facts" };
		var job = Job(chat);
		await job.DrainAllAsync(CancellationToken.None);

		// The hook re-pushes the grown transcript; only the tail is new.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs("old-alpha: initial pass over the config resolver null-reference bug",
				"old-beta: reproduced the crash locally against the staging snapshot",
				"new-gamma: shipped the fix to ci.512 and verified it in the smoke run"));
		chat.NextText = "digest v2\n- old facts\n- new gamma fact";
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(1);

		var prompt = chat.Prompts[^1];
		prompt.Should().Contain("digest v1");        // merge sees the existing digest…
		prompt.Should().Contain("new-gamma");        // …and the delta…
		prompt.Should().NotContain("old-alpha");     // …but not already-distilled messages
		var entry = await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1");
		entry!.Description.Should().Be("digest v2");
		Cursor(entry.Metadata).Should().Be(3);
	}

	[Fact]
	public async Task HotSession_WithinQuietPeriod_IsDeferred()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		var chat = new ChatFake();

		var distilled = await Job(chat, quiet: TimeSpan.FromMinutes(10)).DrainAllAsync(CancellationToken.None);

		distilled.Should().Be(0);
		chat.Prompts.Should().BeEmpty();
	}

	[Fact]
	public async Task ChatUnavailable_PassIsNoOp_AndCursorBackfillsOnRecovery()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1));
		var chat = new ChatFake { Available = false };
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse();

		chat.Available = true; // recovery: the un-advanced cursor re-distills the same delta
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(1);
		(await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1")).Should().NotBeNull();
	}

	[Fact]
	public async Task NoLlmWired_PassIsNoOp()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		(await Job(llm: null).DrainAllAsync(CancellationToken.None)).Should().Be(0);
	}

	[Fact]
	public async Task EmptyChatAnswer_HoldsCursor_NoEntryWritten()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1));
		var chat = new ChatFake { NextText = "   " };

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);
		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse();
	}

	[Fact]
	public async Task LargeBacklog_DrainsFullyInOnePass_NoBatchCap()
	{
		// Message content caps at 4k before batching → 12 messages per 48k batch;
		// 96 messages = 8 batches — the old cap was 6/pass; a generous budget drains ALL.
		var big = new string('ж', 4_000);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs(Enumerable.Range(1, 96).Select(i => $"{i}: {big}").ToArray()));
		var chat = new ChatFake { NextText = "дайджест\n- факт" };

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		chat.Prompts.Count.Should().Be(8); // every batch folded this pass
		Cursor((await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1"))!.Metadata).Should().Be(96);
	}

	[Fact]
	public async Task ZeroBudget_ParksAfterOneBatch_ResumesNextPass()
	{
		// 25 capped messages → batches of 12/12/1.
		var big = new string('ж', 4_000);
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs(Enumerable.Range(1, 25).Select(i => $"{i}: {big}").ToArray()));
		var chat = new ChatFake { NextText = "дайджест\n- факт" };
		var job = Job(chat, budget: TimeSpan.Zero);

		await job.DrainAllAsync(CancellationToken.None);
		chat.Prompts.Count.Should().Be(1); // progress guarantee: exactly one batch, then park
		Cursor((await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1"))!.Metadata).Should().Be(12);

		await job.DrainAllAsync(CancellationToken.None); // resumes where it parked
		chat.Prompts.Count.Should().Be(2);
		Cursor((await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1"))!.Metadata).Should().Be(24);
	}

	[Fact]
	public async Task TwoProjects_ZeroBudget_RotationServesTheOtherNextPass()
	{
		_db.Insert(new Project { Key = "projb", WorkspaceKey = "ws", Name = "B", Description = "" });
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("сессия про деплой конфигурации в проекте А, ci.512 готов"));
		await _sessions.UpsertAsync("projb", "s1", "claude-code", Msgs("сессия про фикс бага векторизации в проекте Б, ci.513 готов"));
		var chat = new ChatFake { NextText = "дайджест\n- факт" };
		var job = Job(chat, budget: TimeSpan.Zero);

		await job.DrainAllAsync(CancellationToken.None);
		var afterFirst = new[] { Proj, "projb" }
			.Count(p => _memory.StoreExistsAsync(p, SessionDigestJob.Store).GetAwaiter().GetResult());
		afterFirst.Should().Be(1); // budget spent on exactly one project

		await job.DrainAllAsync(CancellationToken.None); // round-robin starts at the other
		foreach (var p in new[] { Proj, "projb" })
			(await _memory.StoreExistsAsync(p, SessionDigestJob.Store)).Should().BeTrue();
	}

	[Fact]
	public async Task Discovery_DigestIsFindableThroughMemorySearch()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("we fixed the vectorization crashloop"));
		var chat = new ChatFake { NextText = "Сессия про векторизацию\n- починили crashloop векторизации (b0700d6)" };
		await Job(chat).DrainAllAsync(CancellationToken.None);

		var res = await _memory.SearchAsync(Proj, SessionDigestJob.Store, "crashloop", type: null);

		res.Hits.Select(h => h.Key).Should().Contain("s1"); // discovery = plain memory search over digests
	}

	// --- #1 skip before the LLM: empty/insubstantial sessions never reach chat ---

	[Fact]
	public async Task InsubstantialSession_IsSkippedBeforeChat_NoEntryMinted()
	{
		// A settled session carrying next to no text is the "empty session" the model used
		// to answer "no content to digest"; the substance floor stops it before the call.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("ok"));
		var chat = new ChatFake { NextText = "should never be produced" };

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Prompts.Should().BeEmpty();                                         // no chat spent
		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse(); // no digest minted
	}

	[Fact]
	public async Task InsubstantialTrailingDelta_OverExistingDigest_AdvancesCursorWithoutChat()
	{
		// First a real distill, then the hook re-pushes with a trivial tail ("ok"): the tail
		// is not worth a chat call, but the cursor advances past it so it is not re-examined.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1));
		var chat = new ChatFake { NextText = "Сессия про конфиг\n- факт про фикс" };
		var job = Job(chat);
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(1);
		var callsAfterFirst = chat.Prompts.Count;

		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1, "ok"));
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Prompts.Count.Should().Be(callsAfterFirst);        // trivial tail spent no chat
		var entry = await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1");
		entry!.Description.Should().Be("Сессия про конфиг");    // body untouched
		Cursor(entry.Metadata).Should().Be(2);                  // cursor moved past the tail
	}

	// --- #2 guard after the LLM: a refusal / empty answer is not written ---

	[Fact]
	public async Task LlmRefusal_NewSession_NotWritten_AndWarns()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1));
		var chat = new ChatFake { NextText = "No content to digest." };
		var log = new CapturingLogger();

		(await Job(chat, logger: log).DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Prompts.Should().HaveCount(1);                                     // the model WAS asked…
		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse(); // …but nothing written
		log.Warnings.Should().Contain(w => w.Contains("no usable digest"));
	}

	[Fact]
	public async Task LlmRefusal_OverExistingDigest_AdvancesCursor_KeepsBody()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1));
		var chat = new ChatFake { NextText = "Сессия про конфиг\n- реальный факт" };
		var job = Job(chat);
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(1);

		// A substantial new turn arrives, but the model now refuses: keep the good digest,
		// just move the cursor so the refusal is not retried forever.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1, Big2));
		chat.NextText = "No content to digest.";
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);

		var entry = await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1");
		entry!.Description.Should().Be("Сессия про конфиг");    // prior digest preserved
		entry.Body.Should().Contain("реальный факт");
		Cursor(entry.Metadata).Should().Be(2);                  // advanced past the refused delta
	}

	// --- #3 self-cleanup: junk digests older passes minted are purged on a pass ---

	[Fact]
	public async Task Pass_PurgesExistingJunkDigests_KeepsRealOnes()
	{
		// Seed the store as an older pass would have: one stock-refusal digest, one super-short
		// digest, and one genuine digest. The seed also gives the project a live session so the
		// pass processes it.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1));
		await _memory.UpsertAsync(Proj, SessionDigestJob.Store,
		[
			new MemoryEntryInput { Key = "junk-refusal", Type = "Reference", Description = "No content to digest.", Body = "", Tags = [SessionDigestJob.Tag] },
			new MemoryEntryInput { Key = "junk-short", Type = "Reference", Description = "empty", Body = "", Tags = [SessionDigestJob.Tag] },
			new MemoryEntryInput { Key = "real", Type = "Reference", Description = "Сессия про реальную работу над конфигом resolver", Body = "- починили NRE\n- задеплоили ci.512", Tags = [SessionDigestJob.Tag] },
		], [], ct: CancellationToken.None);

		var chat = new ChatFake { NextText = "Сессия про конфиг\n- факт про фикс" };
		var log = new CapturingLogger();
		await Job(chat, logger: log).DrainAllAsync(CancellationToken.None);

		(await _memory.GetAsync(Proj, SessionDigestJob.Store, "junk-refusal")).Should().BeNull();
		(await _memory.GetAsync(Proj, SessionDigestJob.Store, "junk-short")).Should().BeNull();
		(await _memory.GetAsync(Proj, SessionDigestJob.Store, "real")).Should().NotBeNull();
		log.Warnings.Should().Contain(w => w.Contains("cleanup"));
	}

	// --- #4 the cursor outlives the digest: a refused session is not re-asked forever ---

	[Fact]
	public async Task LlmRefusal_NewSession_SecondPassSpendsNoChat()
	{
		// THE regression. The session has no digest entry (the model answers junk), so back when
		// the cursor rode the entry's metadata there was nowhere to record the position: the
		// session stayed a candidate and burned a chat call EVERY tick, forever (~730/day/session).
		// The cursor now lives in search_cursor and advances regardless → exactly one call, ever.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1));
		var chat = new ChatFake { NextText = "хм" }; // non-empty, but shorter than MinDigestChars
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Prompts.Should().HaveCount(1);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Prompts.Should().HaveCount(1);                                                // no second call
		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse(); // still nothing written
	}

	[Fact]
	public async Task RepeatedRefusals_DeadLetterTheSession_NoFurtherChat()
	{
		// Each pass brings a genuinely new substantial turn, so the advanced cursor alone would
		// keep re-asking. After MaxAttempts refusals the session is dead-lettered and dropped
		// from the candidate set: the (N+1)-th turn spends no chat at all.
		var chat = new ChatFake { NextText = "хм" };
		var log = new CapturingLogger();
		var job = Job(chat, logger: log);

		for (var i = 1; i <= SessionDigestJob.MaxAttempts; i++)
		{
			await _sessions.UpsertAsync(Proj, "s1", "claude-code",
				Msgs(Enumerable.Range(1, i).Select(n => $"{n}: {Big1}").ToArray()));
			await job.DrainAllAsync(CancellationToken.None);
		}
		chat.Prompts.Should().HaveCount(SessionDigestJob.MaxAttempts); // one call per refusal, no more
		log.Warnings.Should().Contain(w => w.Contains("dead-lettered"));

		// One more turn arrives — the dead session is skipped outright.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs(Enumerable.Range(1, SessionDigestJob.MaxAttempts + 1).Select(n => $"{n}: {Big1}").ToArray()));
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Prompts.Should().HaveCount(SessionDigestJob.MaxAttempts);
	}

	[Fact]
	public async Task RepeatedEmptyAnswers_HoldTheCursor_ThenDeadLetterTheSession()
	{
		// The empty-answer branch HOLDS the cursor (a broken response may hide a good delta —
		// it must backfill when chat recovers), so it is a retry by design. The attempt counter
		// is its ceiling: the same session is re-asked at most MaxAttempts times, then dies.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(Big1));
		var chat = new ChatFake { NextText = "   " };
		var log = new CapturingLogger();
		var job = Job(chat, logger: log);

		for (var i = 1; i <= SessionDigestJob.MaxAttempts; i++)
			(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Prompts.Should().HaveCount(SessionDigestJob.MaxAttempts);   // re-asked every pass — cursor held
		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse();
		log.Warnings.Should().Contain(w => w.Contains("dead-lettered"));

		// The ceiling: pass N+1 asks nothing at all — the session is dead.
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);

		chat.Prompts.Should().HaveCount(SessionDigestJob.MaxAttempts);
	}

	[Fact]
	public async Task LegacyDigest_WithMetadataCursor_IsSeeded_NotReDistilledFromZero()
	{
		// Back-compat: an archive digest carries its position in Metadata.cursor and NO row in
		// search_cursor. The first pass adopts that value instead of re-distilling the session
		// from message 1 (which would re-run the LLM over the whole archive).
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs("old-alpha: initial pass over the config resolver null-reference bug",
				"old-beta: reproduced the crash locally against the staging snapshot",
				"new-gamma: shipped the fix to ci.512 and verified it in the smoke run"));
		await _memory.UpsertAsync(Proj, SessionDigestJob.Store, [new MemoryEntryInput
		{
			Key = "s1", Version = 0, Type = "Reference",
			Description = "Сессия про фикс config resolver",
			Body = "- разобрали падение резолвера конфигурации",
			Tags = [SessionDigestJob.Tag],
			Metadata = """{"sessionId":"s1","agent":"claude-code","cursor":2}""",
		}], [], ct: CancellationToken.None);
		var chat = new ChatFake { NextText = "digest v2\n- old facts\n- new gamma fact" };

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		chat.Prompts.Should().HaveCount(1);                      // one merge batch, not a full re-run
		chat.Prompts[0].Should().Contain("new-gamma");           // only the tail was distilled…
		chat.Prompts[0].Should().NotContain("old-alpha");        // …the seeded cursor skipped the rest
		var entry = await _memory.GetAsync(Proj, SessionDigestJob.Store, "s1");
		Cursor(entry!.Metadata).Should().Be(3);
	}

	// Captures warning-level messages so a test can assert the skip/guard/cleanup logged.
	sealed class CapturingLogger : ILogger<SessionDigestJob>
	{
		public List<string> Warnings { get; } = [];
		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
		public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
		public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
			Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
		}

		sealed class NullScope : IDisposable
		{
			public static readonly NullScope Instance = new();
			public void Dispose() { }
		}
	}

	// Chat-capable fake: returns NextText (or a generated digest), records every user
	// prompt, and lets a test flip availability to exercise the degrade path.
	sealed class ChatFake : ILlmClient
	{
		public List<string> Prompts { get; } = [];
		public string? NextText { get; set; }
		public bool Available { get; set; } = true;

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			Prompts.Add(request.Messages[^1].Content);
			var text = NextText ?? $"digest pass {Prompts.Count}\n- fact {Prompts.Count}";
			return Task.FromResult(new ChatResult(text, new ModelIdentity("fake-chat", 0),
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
