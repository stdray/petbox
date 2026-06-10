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
			Tags = "autocaptured",
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
	public async Task BelowThreshold_NoChat_CursorHolds_ThenFiresWhenEnough()
	{
		await SeedFeedback("f1", "s1", "одинокое наблюдение");
		var chat = new ScriptedChat("""[{"description":"когда X — делай Y","body":"b","sources":["s1","s2"]}]""");
		var job = Job(chat);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		chat.Calls.Should().Be(0); // one fresh Feedback < MinNewFeedback

		await SeedFeedback("f2", "s2", "второе наблюдение");
		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(1); // held cursor sees both
		chat.Calls.Should().Be(1);
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
			Tags = "autocaptured,behavior:pattern",
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
				Tags = "autocaptured,behavior:pattern",
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
}
