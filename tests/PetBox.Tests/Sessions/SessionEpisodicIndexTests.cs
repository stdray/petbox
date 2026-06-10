using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Episodic;
using PetBox.Sessions.Services;

namespace PetBox.Tests.Sessions;

// The episodic tier invariants: lazy hydration out of the SESSION STORE (never files),
// russian-stem FTS recall that SQLite prefix-matching cannot give, a semantic leg that
// finds paraphrases, honest degradation when the embedder is down, TTL aging, and
// re-hydration when the session grew past the cached version.
[Collection("DataModule")]
public sealed class SessionEpisodicIndexTests : IDisposable
{
	const string Proj = "proj";

	readonly string _dir;
	readonly ScopedDbFactory<SessionsDb> _factory;
	readonly SessionService _sessions;

	public SessionEpisodicIndexTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-episodic-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_factory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);
		_sessions = new SessionService(new SessionStore(_factory));
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	static SessionMessageInput[] Msgs(params string[] contents) =>
		contents.Select(c => new SessionMessageInput("user", c)).ToArray();

	[Fact]
	public async Task Fts_RussianStemming_MatchesAnotherWordform()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs("вчера мы запустили векторизацию на проде", "обсуждали дизайн дайджеста"));
		using var index = new DuckDbSessionEpisodicIndex(_factory);

		// Query wordform differs from the stored one (запустила vs запустили) — prefix
		// FTS misses it; the russian snowball stemmer is exactly why DuckDB is here.
		var res = await index.SearchAsync(Proj, "s1", "запустила векторизацию", k: 5);

		res.Should().NotBeNull();
		res!.Hits.Should().NotBeEmpty();
		res.Hits[0].Message.Should().Be(1);
		res.Hits[0].Retriever.Should().Be("lexical");
		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse(); // no embedder wired — silently lexical-only
	}

	[Fact]
	public async Task Vector_FindsParaphrase_WithNoSharedTokens()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs($"deploy pipeline {EpisodicEmbedFake.NearQueryMarker}", "совершенно другое сообщение"));
		using var index = new DuckDbSessionEpisodicIndex(_factory, new EpisodicEmbedFake());

		var res = await index.SearchAsync(Proj, "s1", "паравозик", k: 5);

		res!.Retrievers.Semantic.Should().BeTrue();
		res.Hits.Should().Contain(h => h.Message == 1 && h.Retriever == "semantic");
	}

	[Fact]
	public async Task EmbedderDown_DegradesToLexical_Honestly()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("починили баг в индексе"));
		using var index = new DuckDbSessionEpisodicIndex(_factory, new ThrowingEmbedLlm());

		var res = await index.SearchAsync(Proj, "s1", "починили баг", k: 5);

		res!.Hits.Should().NotBeEmpty(); // the lexical leg still answers
		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse();
		res.Retrievers.Degraded.Should().BeTrue(); // and the answer says it is partial
	}

	[Fact]
	public async Task MissingSession_ReturnsNull()
	{
		using var index = new DuckDbSessionEpisodicIndex(_factory);
		(await index.SearchAsync(Proj, "nope", "что угодно", k: 5)).Should().BeNull();
	}

	[Fact]
	public async Task GrownSession_IsRehydrated_NewMessagesSearchable()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("первое сообщение"));
		using var index = new DuckDbSessionEpisodicIndex(_factory);
		(await index.SearchAsync(Proj, "s1", "первое", k: 5))!.Hits.Should().NotBeEmpty();

		// The hook re-pushes the grown transcript; the cached hydration is now stale.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("первое сообщение", "свежая гидрация"));

		var res = await index.SearchAsync(Proj, "s1", "свежая гидрация", k: 5);
		res!.Hits.Should().Contain(h => h.Message == 2);
	}

	[Fact]
	public async Task IdleSession_IsEvictedByTtl()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("сообщение для эвикта"));
		var time = new FakeTimeProvider();
		using var index = new DuckDbSessionEpisodicIndex(_factory, ttl: TimeSpan.FromMinutes(10), time: time);
		await index.SearchAsync(Proj, "s1", "сообщение", k: 5);

		time.Advance(TimeSpan.FromMinutes(11));

		index.EvictIdle().Should().Be(1);
		index.EvictIdle().Should().Be(0); // already gone; the next search re-hydrates
		(await index.SearchAsync(Proj, "s1", "сообщение", k: 5))!.Hits.Should().NotBeEmpty();
	}

	[Fact]
	public async Task OverCapacity_TrimsLeastRecentlyUsed()
	{
		for (var i = 1; i <= 3; i++)
			await _sessions.UpsertAsync(Proj, $"s{i}", "claude-code", Msgs($"сообщение сессии {i}"));
		var time = new FakeTimeProvider();
		using var index = new DuckDbSessionEpisodicIndex(_factory, maxHydrated: 2, time: time);

		for (var i = 1; i <= 3; i++)
		{
			await index.SearchAsync(Proj, $"s{i}", "сообщение", k: 5);
			time.Advance(TimeSpan.FromSeconds(1));
		}

		// s1 (the least recently used) was trimmed when s3 hydrated over the cap of 2;
		// only the eviction already happened inline, so a sweep finds nothing more.
		index.EvictIdle().Should().Be(0);
	}

	[Fact]
	public async Task ReHydration_ReusesPersistedVectors_OnlyTheQueryIsReEmbedded()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("первое сообщение", "второе сообщение"));
		var fake = new EpisodicEmbedFake();
		var time = new FakeTimeProvider();
		using var index = new DuckDbSessionEpisodicIndex(_factory, fake, ttl: TimeSpan.FromMinutes(10), time: time);

		await index.SearchAsync(Proj, "s1", "сообщение", k: 5); // embeds query + both messages
		var inputsAfterFirst = fake.Inputs.Count;
		inputsAfterFirst.Should().Be(3);

		time.Advance(TimeSpan.FromMinutes(11));
		index.EvictIdle().Should().Be(1);

		var res = await index.SearchAsync(Proj, "s1", "сообщение", k: 5); // re-hydration
		res!.Retrievers.Semantic.Should().BeTrue();
		fake.Inputs.Count.Should().Be(inputsAfterFirst + 1);           // only the query went out
		fake.Inputs[^1].Should().Be("сообщение");                       // message vectors came from disk
	}

	[Fact]
	public async Task ChangedOrdinalContent_InvalidatesItsCachedVector()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("старый текст"));
		var fake = new EpisodicEmbedFake();
		using var index = new DuckDbSessionEpisodicIndex(_factory, fake);
		await index.SearchAsync(Proj, "s1", "текст", k: 5);

		// The re-push rewrote ordinal 1's content (and grew the session → re-hydration).
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("совсем новый текст", "хвост"));
		await index.SearchAsync(Proj, "s1", "текст", k: 5);

		fake.Inputs.Should().Contain("совсем новый текст"); // hash mismatch → re-embedded
		fake.Inputs.Should().Contain("хвост");              // new ordinal → embedded
	}

	// Embeds the marker (and any query) onto the same unit vector so a paraphrase with no
	// shared tokens lands adjacent — the same trick as Memory's FakeLlmClient.
	sealed class EpisodicEmbedFake : ILlmClient
	{
		public const string NearQueryMarker = "__NEARQUERY__";
		const int Dim = 8;

		public List<string> Inputs { get; } = [];

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default)
		{
			Inputs.AddRange(request.Inputs);
			var vectors = request.Inputs.Select(text =>
			{
				var v = new float[Dim];
				if (text.Contains(NearQueryMarker) || !text.Contains(' '))
				{
					v[0] = 1f;
					return v;
				}
				var h = unchecked((uint)text.GetHashCode());
				for (var i = 0; i < Dim; i++)
				{
					v[i] = ((h >> i) & 1) == 1 ? 1f : -1f;
					h = h * 2654435761u + 1u;
				}
				return v;
			}).ToList();
			return Task.FromResult(new EmbedResult(vectors, new ModelIdentity("fake-embed", Dim),
				new ServedBy("fake", "fake-embed", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}

	sealed class ThrowingEmbedLlm : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new InvalidOperationException("embed down");
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}
}
