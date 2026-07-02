using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Search;

namespace PetBox.Tests.Memory;

// The pattern-mining promises (spec: autocapture-behavior-patterns): a procedure proven
// by ≥2 distinct sources consolidates into ONE quarantined entry (tag behavior:pattern,
// metadata.sources = every contributing sessionId); a single-source claim is rejected;
// an update extends sources instead of duplicating; mining fires only once enough fresh
// Feedback accumulated; bad LLM output neither crashes nor loops.
[Collection("DataModule")]
public sealed class BehaviorPatternJobTests : IDisposable
{
	const string Proj = "proj";
	const string Store = "autocaptured";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryService _memory;

	public BehaviorPatternJobTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-bpmine-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_memory = new MemoryService(new MemoryStore(_db, _factory), llm: null);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	BehaviorPatternJob Job(ILlmClient? llm) => new(_factory, _memory, llm);

	Task SeedFeedback(string key, string sessionId, string description) =>
		_memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = key, Version = 0, Type = "Feedback",
			Description = description, Body = "наблюдение из " + sessionId,
			Tags = ["autocaptured"],
			Metadata = $"{{\"sessionId\":\"{sessionId}\"}}",
		}], []);

	static IReadOnlyList<MemoryEntryView> Patterns(IReadOnlyList<MemoryEntryView> entries) =>
		entries.Where(e => e.Tags.Contains("behavior:pattern")).ToList();

	[Fact]
	public async Task TwoSources_ConsolidateIntoOnePattern_WithBothSessionIds()
	{
		await SeedFeedback("f1", "s1", "перед merge проверяй git status");
		await SeedFeedback("f2", "s2", "перед merge свежий git status");
		var chat = new ScriptedChat(
			"""[{"description":"когда merge — сначала свежий git status","body":"повторяется в двух сессиях","sources":["s1","s2"]}]""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		var patterns = Patterns(await _memory.ListAsync(Proj, Store, "Feedback"));
		patterns.Should().HaveCount(1);
		patterns[0].Description.Should().Contain("git status");
		patterns[0].Metadata.Should().Contain("s1").And.Contain("s2");
	}

	[Fact]
	public async Task SecondPass_NoNewObservations_NoChat()
	{
		await SeedFeedback("f1", "s1", "ритуал раз");
		await SeedFeedback("f2", "s2", "ритуал два");
		var chat = new ScriptedChat("""[{"description":"когда X — делай Y","body":"b","sources":["s1","s2"]}]""");
		var job = Job(chat);
		await job.DrainAllAsync(CancellationToken.None);
		var calls = chat.Calls;

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(calls); // cursor advanced past our own pattern write too
	}

	[Fact]
	public async Task OneFreshObservation_Triggers_EmptyAnswerAdvancesCursor()
	{
		// One fresh entry MUST trigger: the facts-judge merges repeats into an existing
		// entry, so repetition may never show up as a second fresh row.
		await SeedFeedback("f1", "s1", "одинокое наблюдение");
		var chat = new ScriptedChat("[]");
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(1); // mined, honestly found nothing

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(1); // cursor advanced — no re-burn on the same observation
	}

	[Fact]
	public async Task SingleEntry_WithAccumulatedSeenIn_ProvesRepetitionByItself()
	{
		// The facts-judge merge accumulates seenIn — one entry can carry ≥2 sources.
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "f-merged", Version = 0, Type = "Feedback",
			Description = "когда миграция — сначала бэкап", Body = "повторено",
			Tags = ["autocaptured", "behavior:pattern"],
			Metadata = """{"sessionId":"s2","seenIn":["s2","s1"]}""",
		}], []);
		var chat = new ScriptedChat(
			"""[{"description":"когда миграция — сначала бэкап","body":"из двух сессий","sources":["s1","s2"]}]""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		chat.Prompts[^1].Should().Contain("s1").And.Contain("s2"); // both ids visible as one FACT's sessionIds
		(await _memory.ListAsync(Proj, Store, "Feedback"))
			.Count(e => e.Metadata.Contains("sources")).Should().Be(1);
	}

	[Fact]
	public async Task SingleSourcePattern_IsRejected()
	{
		await SeedFeedback("f1", "s1", "наблюдение раз");
		await SeedFeedback("f2", "s1", "наблюдение два из той же сессии");
		var chat = new ScriptedChat("""[{"description":"когда X — делай Y","body":"b","sources":["s1"]}]""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(0);
		Patterns(await _memory.ListAsync(Proj, Store, "Feedback")).Should().BeEmpty();
	}

	[Fact]
	public async Task Update_ExtendsSources_NoDuplicatePattern()
	{
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "bp-seed", Version = 0, Type = "Feedback",
			Description = "когда деплой — жди CI и смокай", Body = "из s1+s2",
			Tags = ["autocaptured", "behavior:pattern"],
			Metadata = """{"sources":["s1","s2"]}""",
		}], []);
		await SeedFeedback("f3", "s3", "после деплоя дождался CI и прогнал смоук");
		await SeedFeedback("f4", "s4", "снова: деплой → CI → смоук");
		var chat = new ScriptedChat(
			"""[{"key":"bp-seed","description":"когда деплой — жди CI и смокай","body":"подтверждено ещё дважды","sources":["s3","s4"]}]""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		var patterns = Patterns(await _memory.ListAsync(Proj, Store, "Feedback"));
		patterns.Should().HaveCount(1); // merged, not duplicated
		patterns[0].Key.Should().Be("bp-seed");
		foreach (var s in new[] { "s1", "s2", "s3", "s4" })
			patterns[0].Metadata.Should().Contain(s); // sources extended, history kept
	}

	[Fact]
	public async Task ExtractionTaggedSingleSourceEntries_AreObservations_AndConsolidate()
	{
		// Slice 1 already tags single-session procedures behavior:pattern at extraction —
		// they must still count as observations (the prod gap: they were fed to the miner
		// as "existing patterns" with no sources and could never consolidate).
		foreach (var (key, sid) in new[] { ("f1", "s1"), ("f2", "s2") })
			await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
			{
				Key = key, Version = 0, Type = "Feedback",
				Description = "когда миграция — сначала бэкап и checksum", Body = "из " + sid,
				Tags = ["autocaptured", "behavior:pattern"],
				Metadata = $"{{\"sessionId\":\"{sid}\"}}",
			}], []);
		var chat = new ScriptedChat(
			"""[{"description":"когда миграция — сначала бэкап и checksum","body":"повторено в двух сессиях","sources":["s1","s2"]}]""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		chat.Prompts[^1].Should().Contain("\"s1\"").And.Contain("\"s2\""); // both visible as FACTS
		var consolidated = (await _memory.ListAsync(Proj, Store, "Feedback"))
			.Where(e => e.Metadata.Contains("sources")).ToList();
		consolidated.Should().HaveCount(1);
		consolidated[0].Metadata.Should().Contain("s1").And.Contain("s2");
	}

	[Fact]
	public async Task RephrasedNewPattern_DedupsIntoExistingPattern_NoDuplicateRow()
	{
		// The prod bug: the miner re-derives a pattern it already consolidated and emits it as
		// NEW (key omitted, wording drifted) → a second bp-… row. The structural guard must
		// match it to the existing pattern by semantics and turn it into an UPDATE.
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "bp-seed", Version = 0, Type = "Feedback",
			Description = "когда деплой жди CI и прогоняй смоук на проде", Body = "из s1+s2",
			Tags = ["autocaptured", "behavior:pattern"],
			Metadata = """{"sources":["s1","s2"]}""",
		}], []);
		await SeedFeedback("f3", "s3", "наблюдение три про откат миграции");
		await SeedFeedback("f4", "s4", "наблюдение четыре про журнал импорта");
		// Candidate: NO key, description is a rephrase (superset) of bp-seed.
		var chat = new EmbeddingChat(
			"""[{"description":"когда деплой обязательно жди CI и прогоняй смоук на проде","body":"снова подтверждено","sources":["s3","s4"]}]""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		var patterns = Patterns(await _memory.ListAsync(Proj, Store, "Feedback"));
		patterns.Should().HaveCount(1); // deduped into bp-seed, not a second row
		patterns[0].Key.Should().Be("bp-seed");
		foreach (var s in new[] { "s1", "s2", "s3", "s4" })
			patterns[0].Metadata.Should().Contain(s); // sources merged, provenance kept
	}

	[Fact]
	public async Task DistinctPattern_StillCreated_GuardDoesNotOverMerge()
	{
		// The guard must not eat a genuinely different pattern.
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "bp-seed", Version = 0, Type = "Feedback",
			Description = "когда деплой жди CI и прогоняй смоук на проде", Body = "из s1+s2",
			Tags = ["autocaptured", "behavior:pattern"],
			Metadata = """{"sources":["s1","s2"]}""",
		}], []);
		await SeedFeedback("f3", "s3", "линки узлов показывай кликабельным permalink");
		await SeedFeedback("f4", "s4", "снова кликабельный permalink на узел через include_url");
		var chat = new EmbeddingChat(
			"""[{"description":"когда создаёшь узел показывай кликабельный permalink через include_url","body":"иначе владелец не откроет","sources":["s3","s4"]}]""");

		(await Job(chat).DrainAllAsync(CancellationToken.None)).Should().Be(1);

		Patterns(await _memory.ListAsync(Proj, Store, "Feedback")).Should().HaveCount(2); // new, distinct pattern
	}

	[Fact]
	public async Task DedupSweep_FoldsPreExistingTwins_Once_SurvivesRestart()
	{
		// Two near-identical patterns already on disk under different keys (the observed prod
		// state). The one-shot sweep must collapse them into one, merging provenance; a second
		// pass (a restart re-run) must not re-sweep.
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "bp-a", Version = 0, Type = "Feedback",
			Description = "petbox переехал на сервер tun4 apps1 узел", Body = "из s1",
			Tags = ["autocaptured", "behavior:pattern"], Metadata = """{"sources":["s1","s2"]}""",
		}], []);
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "bp-b", Version = 0, Type = "Feedback",
			Description = "petbox теперь переехал на сервер tun4 apps1 узел", Body = "из s3",
			Tags = ["autocaptured", "behavior:pattern"], Metadata = """{"sources":["s2","s3"]}""",
		}], []);
		var chat = new EmbeddingChat("[]"); // mining finds nothing new to consolidate

		await Job(chat).DrainAllAsync(CancellationToken.None);

		var patterns = Patterns(await _memory.ListAsync(Proj, Store, "Feedback"));
		patterns.Should().HaveCount(1); // twins folded
		foreach (var s in new[] { "s1", "s2", "s3" })
			patterns[0].Metadata.Should().Contain(s); // union of both sources

		// Restart re-run: sweep cursor is set, no second collapse, store stable.
		await Job(new EmbeddingChat("[]")).DrainAllAsync(CancellationToken.None);
		Patterns(await _memory.ListAsync(Proj, Store, "Feedback")).Should().HaveCount(1);
	}

	[Fact]
	public async Task MalformedMining_AdvancesCursor_NoCrash_NoBurnLoop()
	{
		await SeedFeedback("f1", "s1", "раз");
		await SeedFeedback("f2", "s2", "два");
		var chat = new ScriptedChat("это не json");
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		var calls = chat.Calls;
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(calls); // cursor moved past the bad pass
	}

	sealed class ScriptedChat(params string[] responses) : ILlmClient
	{
		readonly Queue<string> _queue = new(responses);
		string _last = responses[^1];
		public int Calls { get; private set; }
		public List<string> Prompts { get; } = [];

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			Calls++;
			Prompts.Add(request.Messages[^1].Content);
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

	// Scripted chat that also embeds: deterministic bag-of-words (distinct normalized tokens
	// across the batch = the basis, each input → token-count vector) so cosine is exact BoW
	// similarity — rephrasings that share (nearly) all tokens clear the dedup threshold, a
	// distinct fact stays well below it.
	sealed class EmbeddingChat(params string[] responses) : ILlmClient
	{
		readonly Queue<string> _queue = new(responses);
		string _last = responses[^1];
		public List<string> Prompts { get; } = [];

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
		{
			Prompts.Add(request.Messages[^1].Content);
			if (_queue.Count > 0) _last = _queue.Dequeue();
			return Task.FromResult(new ChatResult(_last, new ModelIdentity("fake-chat", 0),
				new ServedBy("fake", "fake-chat", 1, Degraded: false)));
		}

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(BagOfWords.Embed(request.Inputs));

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}
}

// Deterministic bag-of-words embedder shared by the dedup tests (exact BoW cosine).
static class BagOfWords
{
	public static EmbedResult Embed(IReadOnlyList<string> inputs)
	{
		var vocab = new Dictionary<string, int>();
		foreach (var input in inputs)
			foreach (var tok in Tokenize(input))
				if (!vocab.ContainsKey(tok)) vocab[tok] = vocab.Count;
		var dim = Math.Max(vocab.Count, 1);
		var vectors = inputs.Select(input =>
		{
			var v = new float[dim];
			foreach (var tok in Tokenize(input)) v[vocab[tok]] += 1f;
			return v;
		}).ToList();
		return new EmbedResult(vectors, new ModelIdentity("fake-embed", 0),
			new ServedBy("fake", "fake-embed", 1, Degraded: false));
	}

	static IEnumerable<string> Tokenize(string? s) =>
		(s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.Select(t => new string(t.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()))
			.Where(t => t.Length > 0);
}
