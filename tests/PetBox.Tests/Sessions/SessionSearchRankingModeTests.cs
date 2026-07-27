using LinqToDB;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Episodic;
using PetBox.Sessions.Search;
using PetBox.Sessions.Services;
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Tests.Sessions;

// search-rerank-for-sessions: sessions carry a cross-encoder rerank on BOTH stages of the
// two-stage pipeline (discovery over the digest store via MemoryService.SearchScoredAsync,
// and episodic hydration via DuckDbSessionEpisodicIndex) — but until this card, BOTH stages
// were hardcoded Precision and ignored the caller's chosen SearchRankingMode. These tests
// wire a REAL reranker (counting calls) into both stages via SessionSearchService.SearchAsync's
// `mode` parameter and assert:
//   - an explicit Speed ask never touches the cross-encoder on EITHER stage, and both
//     provenance surfaces (the discovery-leg envelope AND each candidate's own episodic
//     Retrievers) honestly report ChosenRrf, not Reranked/DegradedRrf;
//   - the default (Precision, mode omitted — matching the MCP edge default) DOES run the
//     cross-encoder on BOTH stages and reports Reranked on both provenance surfaces.
// Reverting the `mode` threading in SessionSearchService/MemoryService/DuckDbSessionEpisodicIndex
// turns the Speed test red (both stages would silently stay hardcoded Precision and rerank
// anyway), proving these tests actually guard the caller's ranking-mode choice, not just its
// plumbing.
public sealed class SessionSearchRankingModeTests : IDisposable
{
	const string Proj = "proj";
	static readonly TimeSpan NoQuiet = TimeSpan.FromMinutes(-5);

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<SessionsDb> _sessionsFactory;
	readonly ScopedDbFactory<MemoryDb> _memoryFactory;
	readonly SessionService _sessions;
	readonly RerankCountingLlmClient _rerankLlm;
	readonly MemoryService _memory;
	readonly DuckDbSessionEpisodicIndex _episodic;
	readonly SessionTermIndex _termIndex;
	readonly SessionFullScanIndex _fullScanIndex;
	readonly ISettingsResolver _settingsResolver;
	readonly SessionSearchService _search;

	public SessionSearchRankingModeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessrankmode-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_sessionsFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), TestSchema.Sessions);
		_memoryFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_sessions = new SessionService(new SessionStore(_sessionsFactory));
		// The SAME reranking-capable client backs BOTH stages — a real LlmClientReranker is
		// constructed from it on each stage's own search, so RerankCalls counts cross-encoder
		// invocations across the whole two-stage walk (never IsAvailableAsync alone).
		_rerankLlm = new RerankCountingLlmClient();
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _memoryFactory), llm: _rerankLlm);
		_episodic = new DuckDbSessionEpisodicIndex(_sessionsFactory, llm: _rerankLlm);
		_termIndex = new SessionTermIndex(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions);
		_fullScanIndex = new SessionFullScanIndex(_sessions);
		_settingsResolver = new SettingsResolver(new SettingsStore(_db.Factory()), new NoSecrets());
		_search = new SessionSearchService(_memory, _episodic, _termIndex, _fullScanIndex, _settingsResolver, _sessions);
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

	// Distillation uses a SEPARATE chat-only client (mirrors SessionSearchServiceTests.EchoChat) —
	// the digest job's own llm parameter is independent of `_memory`'s wired reranker, so writing
	// digests never touches _rerankLlm's counters.
	Task<int> Distill() =>
		new SessionDigestJob(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _memory, new EchoChat(),
				logger: null, quietPeriod: NoQuiet)
			.DrainAllAsync(CancellationToken.None);

	async Task SeedOneDistilledSessionAsync()
	{
		await _sessions.UpsertAsync(Proj, "s-vec", "claude-code",
			Msgs("обсуждали план на неделю", "мы запустили векторизацию на проде в ci.300"));
		(await Distill()).Should().Be(1);
	}

	[Fact]
	public async Task Speed_SkipsCrossEncoder_OnBothStages_ReportsChosenRrf()
	{
		await SeedOneDistilledSessionAsync();

		var res = await _search.SearchAsync(Proj, "запустили векторизацию", mode: SearchRankingMode.Speed);

		res.Candidates.Should().NotBeEmpty();
		res.Discovery.Ranking.Should().Be(SearchRankingOutcome.ChosenRrf,
			"stage 1 (digest discovery) must honor an explicit Speed ask, not stay hardcoded Precision");
		res.Candidates[0].Retrievers.Ranking.Should().Be(SearchRankingOutcome.ChosenRrf,
			"stage 2 (episodic hydration) must honor the SAME Speed ask");
		_rerankLlm.RerankCalls.Should().Be(0,
			"Speed must never touch the cross-encoder on EITHER stage");
	}

	[Fact]
	public async Task Precision_DefaultMode_RunsCrossEncoder_OnBothStages_ReportsReranked()
	{
		await SeedOneDistilledSessionAsync();

		// `mode` omitted — the default (Precision), the same edge default session_search (MCP) uses.
		var res = await _search.SearchAsync(Proj, "запустили векторизацию");

		res.Candidates.Should().NotBeEmpty();
		res.Discovery.Ranking.Should().Be(SearchRankingOutcome.Reranked,
			"stage 1 (digest discovery) must run the cross-encoder under Precision when a route is live");
		res.Candidates[0].Retrievers.Ranking.Should().Be(SearchRankingOutcome.Reranked,
			"stage 2 (episodic hydration) must ALSO run the cross-encoder under Precision");
		_rerankLlm.RerankCalls.Should().BeGreaterThanOrEqualTo(2,
			"the cross-encoder must fire once per stage — a caller paying for Precision on sessions pays it on BOTH legs");
	}

	// No-op secret encryptor: the settings exercised here have no [Setting(IsSecret=true)]
	// properties, so encryption is never reached — only present to satisfy the constructor.
	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}

	// Chat fake whose digest echoes the distilled messages (mirrors SessionSearchServiceTests.EchoChat),
	// so the digest carries the session's distinctive tokens the discovery-leg query below matches.
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

	// A deterministic ILlmClient wired for BOTH capabilities the two search stages need
	// (Embed for the semantic leg, Rerank for the cross-encoder) — mirrors
	// MemoryVisibilityParityTests.RerankingLlmClient's call-counting posture, generalized to
	// double as the episodic index's client too. RerankAsync keeps the fused order (identity
	// reorder) — these tests only assert WHETHER the cross-encoder ran and what provenance it
	// reported, never a specific reordering.
	sealed class RerankCountingLlmClient : ILlmClient
	{
		public int RerankCalls;

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(new EmbedResult(
				request.Inputs.Select(_ => new float[] { 1f }).ToList(),
				new ModelIdentity("rc-embed", 1), new ServedBy("fake", "rc-embed", 1, Degraded: false)));

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default)
		{
			Interlocked.Increment(ref RerankCalls);
			var hits = request.Documents
				.Select((_, i) => new RerankHit(i, -i)) // identity order — no reordering, just counted
				.Take(request.TopN ?? request.Documents.Count)
				.ToList();
			return Task.FromResult(new RerankResult(hits, new ModelIdentity("rc-rerank"), new ServedBy("fake", "rc-rerank", 1, Degraded: false)));
		}

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}
}
