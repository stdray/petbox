using LinqToDB;
using Microsoft.Extensions.Options;
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
		TestSchema.Core(cs);
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
		TestDirs.CleanupOrDefer(_dir);
	}

	BehaviorPatternJob Job(ILlmClient? llm, AutocaptureDedupOptions? dedup = null) =>
		new(_factory, _memory, llm, dedup: dedup is null ? null : Options.Create(dedup));

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
	public async Task OneFreshObservation_DoesNotTrigger_SecondObservationDoes()
	{
		// A pattern is REPETITION → it needs ≥MinNewFeedback (2) fresh observations. One lone
		// entry doesn't burn a mine-chat; the cursor holds until a second observation lands.
		await SeedFeedback("f1", "s1", "одинокое наблюдение");
		var chat = new ScriptedChat("[]");
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(0); // gate held — no chat spent on a single observation

		await SeedFeedback("f2", "s2", "второе наблюдение");
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(1); // two fresh now → mined, honestly found nothing
	}

	[Fact]
	public async Task MergedEntrysSessionIds_AreVisibleToMiner_OnceGateClears()
	{
		// The facts-judge merge accumulates seenIn, so one entry can carry ≥2 sessionIds; the
		// miner sees them as one FACT's sessionIds. The ≥2-fresh-rows gate still needs a second
		// observation before firing — supplied here by a plain second feedback.
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "f-merged", Version = 0, Type = "Feedback",
			Description = "когда миграция — сначала бэкап", Body = "повторено",
			Tags = ["autocaptured", "behavior:pattern"],
			Metadata = """{"sessionId":"s2","seenIn":["s2","s1"]}""",
		}], []);
		await SeedFeedback("f2", "s3", "ещё одно наблюдение про миграции");
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
		// as "existing patterns" with no sources and could never consolidate). The two phrasings
		// differ so the deterministic sweep leaves them as two distinct observations (the miner
		// is what consolidates a repeated PROCEDURE, not the exact-text collapse).
		foreach (var (key, sid, desc) in new[]
				{
					("f1", "s1", "когда миграция сначала делай бэкап и checksum"),
					("f2", "s2", "перед миграцией сперва бэкап потом проверка checksum"),
				})
			await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
			{
				Key = key, Version = 0, Type = "Feedback",
				Description = desc, Body = "из " + sid,
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
	public async Task DedupSweep_IsPeriodic_ReRunsAfterInterval_NotOnceForever()
	{
		// W2: the sweep is PERIODIC, not one-shot. With a 0-day interval it re-runs every pass,
		// so twins that appear AFTER the first sweep are still folded (background consolidation).
		var opts = new AutocaptureDedupOptions { RecollapseIntervalDays = 0 };
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

		await Job(new EmbeddingChat("[]"), opts).DrainAllAsync(CancellationToken.None);
		Patterns(await _memory.ListAsync(Proj, Store, "Feedback")).Should().HaveCount(1); // first pair folded

		// A NEW pair of twins lands after the first sweep. A one-shot sweep would leave them; a
		// periodic one folds them on the next pass.
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "bp-c", Version = 0, Type = "Feedback",
			Description = "бэкапы хранятся только локально на сервере петбокса", Body = "из s4",
			Tags = ["autocaptured", "behavior:pattern"], Metadata = """{"sources":["s4","s5"]}""",
		}], []);
		await _memory.UpsertAsync(Proj, Store, [new MemoryEntryInput
		{
			Key = "bp-d", Version = 0, Type = "Feedback",
			Description = "бэкапы хранятся сейчас только локально на сервере петбокса", Body = "из s6",
			Tags = ["autocaptured", "behavior:pattern"], Metadata = """{"sources":["s5","s6"]}""",
		}], []);

		await Job(new EmbeddingChat("[]"), opts).DrainAllAsync(CancellationToken.None);
		Patterns(await _memory.ListAsync(Proj, Store, "Feedback")).Should().HaveCount(2); // second pair folded too
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

// Deterministic, BATCH-INDEPENDENT bag-of-words embedder shared by the dedup tests: each token
// maps to a FIXED dimension (FNV-1a hash mod dim), so a text always embeds to the same vector
// regardless of the batch it rode in. A per-call vocab would give a cached text a different
// basis and break the per-pass embedding cache (sweep embeds descriptions, then mining re-embeds).
static class BagOfWords
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
