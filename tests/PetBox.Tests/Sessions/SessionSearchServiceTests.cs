using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Episodic;
using PetBox.Sessions.Services;
using PetBox.Web.Search;

namespace PetBox.Tests.Sessions;

// The full two-stage pipeline wired end-to-end in-process: sessions are pushed →
// SessionDigestJob distills digests into memory → SessionSearchService discovers the
// right session through the digest store and drills inside it through the episodic
// index, returning message-ordinal provenance.
public sealed class SessionSearchServiceTests : IDisposable
{
	const string Proj = "proj";
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly ScopedDbFactory<MemoryDb> _memoryFactory;
	readonly SessionService _sessions;
	readonly MemoryService _memory;
	readonly DuckDbSessionEpisodicIndex _episodic;
	readonly SessionSearchService _search;

	public SessionSearchServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sesssearch-" + Guid.NewGuid().ToString("N"));
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
		_memory = new MemoryService(new MemoryStore(_db, _memoryFactory), llm: null);
		_episodic = new DuckDbSessionEpisodicIndex(_sessionsFactory);
		_search = new SessionSearchService(_memory, _episodic);
	}

	public void Dispose()
	{
		_episodic.Dispose();
		_db.Dispose();
		_sessionsFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_memoryFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static SessionMessageInput[] Msgs(params string[] contents) =>
		contents.Select(c => new SessionMessageInput("user", c)).ToArray();

	Task<int> Distill() =>
		new SessionDigestJob(_sessionsFactory, _sessions, _memory, new EchoChat(), logger: null, quietPeriod: NoQuiet)
			.DrainAllAsync(CancellationToken.None);

	[Fact]
	public async Task TwoStage_FindsTheRightSession_AndTheMessageInsideIt()
	{
		await _sessions.UpsertAsync(Proj, "s-vec", "claude-code",
			Msgs("обсуждали план на неделю", "мы запустили векторизацию на проде в ci.300"));
		await _sessions.UpsertAsync(Proj, "s-ui", "claude-code",
			Msgs("правили вёрстку дашборда и тёмную тему"));
		(await Distill()).Should().Be(2);

		var res = await _search.SearchAsync(Proj, "запустили векторизацию");

		res.Distilled.Should().BeTrue();
		res.Reason.Should().BeNull();                          // distilled → no reason code
		res.Discovery.Lexical.Should().BeTrue();
		res.Candidates.Should().NotBeEmpty();
		var top = res.Candidates[0];
		top.SessionId.Should().Be("s-vec");                    // stage 1 — discovered via the digest
		top.Hits.Should().NotBeEmpty();
		top.Hits[0].Message.Should().Be(2);                    // stage 2 — the verbatim message inside
		top.Hits[0].Snippet.Should().Contain("векторизацию");
		top.Retrievers.Lexical.Should().BeTrue();
	}

	[Fact]
	public async Task NoDigestStoreYet_SaysNotDistilled_InsteadOfFailing()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("что-то"));

		var res = await _search.SearchAsync(Proj, "что-то");

		res.Distilled.Should().BeFalse();
		res.Reason.Should().Be("no-digest-store");             // structural signal, not a bare bool
		res.Candidates.Should().BeEmpty();
	}

	[Fact]
	public async Task SessionDeletedAfterDistillation_StaleDigestCandidateIsSkipped()
	{
		await _sessions.UpsertAsync(Proj, "s-gone", "claude-code", Msgs("уникальный термин шарманка"));
		(await Distill()).Should().Be(1);
		await _sessions.DeleteAsync(Proj, "s-gone");

		var res = await _search.SearchAsync(Proj, "шарманка");

		res.Distilled.Should().BeTrue();
		res.Candidates.Should().BeEmpty(); // discovered but unhydratable — skipped, not an error
	}

	// Chat fake whose digest echoes the distilled messages, so the digest carries the
	// session's distinctive tokens (what the real facts-distillation prompt asks for).
	sealed class EchoChat : ILlmClient
	{
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			var prompt = request.Messages[^1].Content;
			var at = prompt.IndexOf("NEW MESSAGES:", StringComparison.Ordinal);
			var body = (at < 0 ? prompt : prompt[at..])
				.Replace("NEW MESSAGES:", "").Replace("[user]", "").Trim();
			var firstLine = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.FirstOrDefault() ?? "сессия";
			return Task.FromResult(new ChatResult($"Сессия: {firstLine}\n- {body.ReplaceLineEndings(" ")}",
				new ModelIdentity("fake-chat", 0), new ServedBy("fake", "fake-chat", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}
}
