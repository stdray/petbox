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

// The discovery tier of session search: the digest job distills a session's transcript
// into the project's `session-digests` memory store, incrementally (the digest entry's
// metadata cursor feeds ISessionService.DeltaAsync), without ever re-distilling an
// unchanged session, and degrades to a no-op when chat is unavailable.
[Collection("DataModule")]
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

	SessionDigestJob Job(ILlmClient? llm, TimeSpan? quiet = null) =>
		new(_sessionsFactory, _sessions, _memory, llm, logger: null, quietPeriod: quiet ?? NoQuiet);

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
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a", "b"));
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
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("old-alpha", "old-beta"));
		var chat = new ChatFake { NextText = "digest v1\n- old facts" };
		var job = Job(chat);
		await job.DrainAllAsync(CancellationToken.None);

		// The hook re-pushes the grown transcript; only the tail is new.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("old-alpha", "old-beta", "new-gamma"));
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
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
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
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("a"));
		var chat = new ChatFake { NextText = "   " };

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);
		(await _memory.StoreExistsAsync(Proj, SessionDigestJob.Store)).Should().BeFalse();
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
