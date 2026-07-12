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
		TestDirs.CleanupOrDefer(_dir);
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
		// Substantive (>= the 30-char semantic floor) so the real feature does not exclude it —
		// a genuine paraphrase-bearing message is never this short in practice.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code",
			Msgs($"deploy the whole release pipeline today {EpisodicEmbedFake.NearQueryMarker}", "совершенно другое сообщение"));
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
		// MinSemanticChars=0: this test exercises the vector CACHE, so every (short) message must
		// embed — the junk-length gate is orthogonal and off here.
		using var index = new DuckDbSessionEpisodicIndex(_factory, fake, ttl: TimeSpan.FromMinutes(10), time: time,
			options: new SessionEpisodicOptions { MinSemanticChars = 0 });

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
		// MinSemanticChars=0: this test asserts on per-ordinal embed calls (hash invalidation +
		// new ordinal), so the short messages must reach the embedder.
		using var index = new DuckDbSessionEpisodicIndex(_factory, fake,
			options: new SessionEpisodicOptions { MinSemanticChars = 0 });
		await index.SearchAsync(Proj, "s1", "текст", k: 5);

		// The re-push rewrote ordinal 1's content (and grew the session → re-hydration).
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs("совсем новый текст", "хвост"));
		await index.SearchAsync(Proj, "s1", "текст", k: 5);

		fake.Inputs.Should().Contain("совсем новый текст"); // hash mismatch → re-embedded
		fake.Inputs.Should().Contain("хвост");              // new ordinal → embedded
	}

	[Fact]
	public async Task Semantic_ExcludesJunk_QuotaFilledWithSubstantiveHits()
	{
		// Two substantive marker-messages the semantic leg should surface, interleaved with the
		// trivial service messages ("```", "Записано.", "No response requested.") that the plain
		// hybrid would otherwise float to rank 0 of the semantic leg.
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(
			$"deploy the whole pipeline {EpisodicEmbedFake.NearQueryMarker} мы настроили и проверили конвейер", // 1 substantive
			"```",                                                                                              // 2 junk
			"Записано.",                                                                                        // 3 junk
			$"второй содержательный ответ {EpisodicEmbedFake.NearQueryMarker} про архитектуру и дизайн решения", // 4 substantive
			"No response requested."));                                                                         // 5 junk
		using var index = new DuckDbSessionEpisodicIndex(_factory, new EpisodicEmbedFake());

		var res = await index.SearchAsync(Proj, "s1", "паравозик", k: 2);

		res!.Retrievers.Semantic.Should().BeTrue();
		// The k=2 quota is filled by the substantive hits (over-fetch refills past the excluded
		// junk); no junk ordinal survives.
		res.Hits.Should().HaveCount(2);
		res.Hits.Should().OnlyContain(h => h.Message == 1 || h.Message == 4);
	}

	[Fact]
	public async Task LexicalMatch_OnShortMessage_IsNeverFloored()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(
			"длинное содержательное сообщение совсем про другую тему без нужного токена", // 1 substantive, no match
			"деплой ок"));                                                                // 2 short (<30), lexical target
		using var index = new DuckDbSessionEpisodicIndex(_factory, new EpisodicEmbedFake());

		var res = await index.SearchAsync(Proj, "s1", "деплой", k: 5);

		// The short message is kept OUT of the semantic leg but the lexical leg still indexes it;
		// a precise BM25 token hit on a short message must survive.
		res!.Hits.Should().Contain(h => h.Message == 2 && h.Retriever == "lexical");
	}

	[Fact]
	public async Task JunkMessages_AreNotEmbedded_NoWastedCallsOrCacheRows()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(
			$"содержательное сообщение {EpisodicEmbedFake.NearQueryMarker} с реальным смыслом внутри", // 1 substantive
			"```",                                                                                     // 2 junk
			"Записано."));                                                                             // 3 junk
		var fake = new EpisodicEmbedFake();
		using var index = new DuckDbSessionEpisodicIndex(_factory, fake);

		await index.SearchAsync(Proj, "s1", "паравозик", k: 5);

		fake.Inputs.Should().NotContain("```");
		fake.Inputs.Should().NotContain("Записано.");
		fake.Inputs.Should().Contain(c => c.Contains("реальным смыслом")); // the substantive one embedded
																		   // And no message_vec cache rows were spent on the junk — only the one substantive message.
		_factory.GetDb(Proj).MessageVectors.Count(v => v.SessionId == "s1").Should().Be(1);
	}

	[Fact]
	public async Task SemanticOnlyHit_BelowFloor_IsDropped_UnlessDisabled()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", Msgs(
			$"уникальное содержательное сообщение про дизайн и планы {EpisodicEmbedFake.NearQueryMarker}"));

		// A lone semantic-only hit fuses to 1/60 ≈ 0.0167 at rank 0; a floor above that drops it —
		// the score floor CAN trim a rank-0 hit, it just cannot catch junk cheaper than the floor.
		using var strict = new DuckDbSessionEpisodicIndex(_factory, new EpisodicEmbedFake(),
			options: new SessionEpisodicOptions { SemanticFloor = 0.02 });
		var dropped = await strict.SearchAsync(Proj, "s1", "паравозик", k: 5);
		dropped!.Hits.Should().BeEmpty();

		// Disabling both knobs (0) restores the old behavior — the semantic hit survives.
		using var lax = new DuckDbSessionEpisodicIndex(_factory, new EpisodicEmbedFake(),
			options: new SessionEpisodicOptions { SemanticFloor = 0, MinSemanticChars = 0 });
		var kept = await lax.SearchAsync(Proj, "s1", "паравозик", k: 5);
		kept!.Hits.Should().Contain(h => h.Message == 1 && h.Retriever == "semantic");
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
