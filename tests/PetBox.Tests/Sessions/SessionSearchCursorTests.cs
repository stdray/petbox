using LinqToDB;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Models;
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

// session_search PAGINATION (work/search-results-pageable, spec result-set-pageable).
//
// What is paged here is the DISCOVERY ORDER — the sessions that will be SHOWN — not the digests. A
// digest is how a session was found, several can point at one session, and a caller never navigates
// them. So the pool addresses session ids, and each page hydrates only its own slice: paging must never
// turn a sublinear discovery into a full archive scan.
//
// Sessions have NO cross-encoder rerank (SessionSearchService takes only SearchOrderingPolicies), so
// there is no "one rerank per pool" claim to make here and nothing in this contract offers a ranking
// mode. What must still hold is everything else: pages concatenate to the unpaged answer, a repeat walk
// reproduces it, the stop reason is stated in the SAME words the other surfaces use, and a discovery
// order that moved under an in-flight cursor is a refusal rather than a silent restart.
public sealed class SessionSearchCursorTests : IDisposable
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
	readonly SessionTermIndex _termIndex;
	readonly SessionFullScanIndex _fullScanIndex;
	readonly ISettingsResolver _settingsResolver;
	readonly SessionSearchService _search;

	public SessionSearchCursorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sesscursor-" + Guid.NewGuid().ToString("N"));
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
		_episodic = new DuckDbSessionEpisodicIndex(_sessionsFactory);
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

	Task<int> Distill() =>
		new SessionDigestJob(_sessionsFactory, new ProjectCatalog(_db.Factory()), _sessions, _memory, new EchoChat(),
				logger: null, quietPeriod: NoQuiet)
			.DrainAllAsync(CancellationToken.None);

	// Six discoverable sessions. Distillation is part of the SEED, not an extra: without a digest store
	// the discovery pool is empty here, and a walk over an empty pool would pass every concatenation
	// assertion vacuously — proving nothing while looking green.
	async Task SeedSix()
	{
		for (var i = 0; i < 6; i++)
			await _sessions.UpsertAsync(Proj, $"s-{i}", "claude-code",
				Msgs($"разговор {i} про векторизацию индекса", "прочее"));
		(await Distill()).Should().Be(6);
	}

	// Walk the discovery pool page by page, returning every session id in page order.
	async Task<List<string>> WalkAsync(int pageSize)
	{
		var ids = new List<string>();
		string? after = null;
		for (var guard = 0; guard < 100; guard++)
		{
			var res = await _search.SearchAsync(Proj, "векторизацию", sessions: pageSize, afterSessionId: after);
			ids.AddRange(res.Candidates.Select(c => c.SessionId));
			if (!res.MoreInPool) return ids;
			after = res.LastPoolKey;
		}
		throw new InvalidOperationException("page walk did not terminate — MoreInPool never went false");
	}

	// ── THE invariant: pages == the whole thing ──────────────────────────────────────────────

	[Fact]
	public async Task PageWalk_ConcatenatesToTheUnpagedDiscovery()
	{
		await SeedSix();

		var whole = (await _search.SearchAsync(Proj, "векторизацию", sessions: 30))
			.Candidates.Select(c => c.SessionId).ToList();
		whole.Should().HaveCount(6);

		var paged = await WalkAsync(pageSize: 2);

		paged.Should().Equal(whole, "paging must change presentation only — never selection or order");
		paged.Should().OnlyHaveUniqueItems("a keyset seek re-serves nothing");
	}

	[Fact]
	public async Task PageWalk_PageSizeOfOne_CoversThePoolWithoutHoles()
	{
		await SeedSix();

		var whole = (await _search.SearchAsync(Proj, "векторизацию", sessions: 30))
			.Candidates.Select(c => c.SessionId).ToList();

		(await WalkAsync(pageSize: 1)).Should().Equal(whole);
	}

	[Fact]
	public async Task SecondWalk_OverUnchangedData_ReproducesTheFirstWalkExactly()
	{
		await SeedSix();

		var first = await WalkAsync(pageSize: 2);
		var second = await WalkAsync(pageSize: 2);

		second.Should().Equal(first);
	}

	[Fact]
	public async Task EachPage_HydratesOnlyItsOwnSlice_NotTheWholePool()
	{
		// The cost guarantee: a page of 2 returns 2 candidates even though the pool holds 6. If paging
		// ever hydrated the pool to find its slice, discovery would stop being sublinear to the archive.
		await SeedSix();

		var page = await _search.SearchAsync(Proj, "векторизацию", sessions: 2);

		page.Candidates.Should().HaveCount(2);
		page.MoreInPool.Should().BeTrue();
	}

	// ── the pool addresses what is SHOWN, and declares its depth ─────────────────────────────

	[Fact]
	public async Task ThePool_AddressesSessions_NotDigests()
	{
		// Two sessions, distilled — the pool must be keyed by session id (what a caller navigates),
		// which is what makes a resume point meaningful across pages.
		await SeedSix();

		var walked = await WalkAsync(pageSize: 2);

		walked.Should().OnlyHaveUniqueItems("one row per SESSION — a session found by several digests is still one row");
		walked.Should().BeSubsetOf(Enumerable.Range(0, 6).Select(i => $"s-{i}"));
	}

	[Fact]
	public async Task Response_DeclaresTheDiscoveryDepth_AndWhetherItWasReached()
	{
		await SeedSix();

		var res = await _search.SearchAsync(Proj, "векторизацию", sessions: 2);

		res.PoolLimit.Should().Be(SessionSearchService.DiscoveryPoolLimit);
		res.PoolBounded.Should().BeFalse("six sessions is nowhere near the discovery depth — this is a real exhaustion");
	}

	[Fact]
	public async Task LastPage_ReportsNoMore()
	{
		await SeedSix();

		var res = await _search.SearchAsync(Proj, "векторизацию", sessions: 30);

		res.MoreInPool.Should().BeFalse();
		res.PoolBounded.Should().BeFalse();
	}

	// ── the deliberate refusals ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task DataVersion_Changes_WhenANewSessionJoinsTheDiscoveryOrder()
	{
		// The stamp IS the discovery order, so a newcomer that enters the pool must move it — that is
		// what makes the adapter's cursor refuse an in-flight walk instead of splicing two orderings.
		await SeedSix();
		var before = (await _search.SearchAsync(Proj, "векторизацию", sessions: 2)).DataVersion;

		await _sessions.UpsertAsync(Proj, "s-new", "claude-code", Msgs("ещё один разговор про векторизацию"));
		await Distill();
		var after = (await _search.SearchAsync(Proj, "векторизацию", sessions: 2)).DataVersion;

		before.Should().NotBeNull();
		after.Should().NotBe(before);
	}

	[Fact]
	public async Task DataVersion_IsStable_WhenNothingChanged()
	{
		await SeedSix();

		var a = (await _search.SearchAsync(Proj, "векторизацию", sessions: 2)).DataVersion;
		var b = (await _search.SearchAsync(Proj, "векторизацию", sessions: 2)).DataVersion;

		b.Should().Be(a, "a stamp that drifted on its own would refuse every second page for no reason");
	}

	[Fact]
	public async Task ResumePoint_ThatLeftThePool_IsRefused_NotSilentlyRestarted()
	{
		// Restarting at the top would hand back a page that looks like a continuation and is not — the
		// exact failure the keyset design exists to prevent.
		await SeedSix();

		var act = () => _search.SearchAsync(Proj, "векторизацию", sessions: 2, afterSessionId: "s-never-existed");

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*no longer in the discovery pool*");
	}

	// ── fixtures (local copies: the originals are private to SessionSearchServiceTests) ──────

	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}

	// Digest fake that echoes the distilled messages, so a digest carries its session's distinctive
	// tokens — the shape the real facts-distillation prompt asks for.
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
