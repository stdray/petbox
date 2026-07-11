using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Time.Testing;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using PetBox.LlmRouter.Registry;
using PetBox.LlmRouter.Routing;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Search;

// The production hole this suite exists to reproduce: a project with NO Embed route had a
// STRUCTURALLY DEAD semantic leg — every search threw inside the vector index, was swallowed by a
// bare `catch { degraded = true; }`, and answered a mute `degraded:true`. Nothing was logged
// anywhere, so it was invisible for as long as nobody went looking. Here the same setup must
// (a) tell the caller WHY (degradedReason = embed-no-route) and (b) leave a log event behind
// (spec: search-provenance; search-semantic-optional keeps the degradation non-fatal).
public sealed class SearchProvenanceTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;

	public SearchProvenanceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-searchprov-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db, _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static MemoryEntryInput Entry(string key, string description, string body) =>
		new() { Key = key, Version = 0, Type = "Project", Description = description, Body = body };

	// A REAL router over an EMPTY registry — exactly the boat every project outside $system was in.
	static CapabilityRouter NoRouteRouter(ILogger<CapabilityRouter> log) =>
		new(new EmptyResolver(), new CertPinningHttpClientProvider(), new UnusedUpstream(),
			new EndpointBreaker(new FakeTimeProvider()), log);

	[Fact]
	public async Task MemorySearch_WithNoEmbedRoute_ReportsReason_AndLogsIt()
	{
		var routerLog = new CapturingLogger<CapabilityRouter>();
		var memoryLog = new CapturingLogger<MemoryService>();
		var memory = new MemoryService(_store, NoRouteRouter(routerLog), rerank: null, log: memoryLog);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("alpha", "alpha note", "alpha keyword")], []);

		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null);

		// Behaviour is UNCHANGED: the lexical floor still answers (degradation, not failure).
		res.Hits.Select(h => h.Key).Should().Equal("alpha");
		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse();
		// …but the answer now says WHY it is degraded, with a stable machine code.
		res.Retrievers.Degraded.Should().BeTrue();
		res.Retrievers.DegradedReason.Should().Be(SearchDegradedReason.EmbedNoRoute);

		// …and the hole is no longer silent, on both sides of the seam.
		var routed = routerLog.Entries.Should().ContainSingle(e => e.EventId == 305).Which;
		routed.Level.Should().Be(MsLogLevel.Warning);
		routed.Message.Should().Contain("NO ROUTE").And.Contain(Proj);

		var degraded = memoryLog.Entries.Should().ContainSingle(e => e.EventId == 400).Which;
		degraded.Level.Should().Be(MsLogLevel.Warning);
		degraded.Message.Should().Contain(SearchDegradedReason.EmbedNoRoute).And.Contain(nameof(VectorSearchIndex));
	}

	[Fact]
	public async Task TransientEmbedFailure_ReportsTransientReason_NotNoRoute()
	{
		// One endpoint, refused: the chain is exhausted transiently. A caller must be able to tell
		// "the embedder blipped, retry" from "this project has no embed route, semantic is dead".
		var reg = new ResolvedRegistry(
			new LlmRegistry([new LlmEndpoint("p", "https://p")], [new LlmRoute(LlmCapability.Embed, "p", "m", 10)]),
			new Dictionary<string, string>());
		var router = new CapabilityRouter(new FixedResolver(reg), new CertPinningHttpClientProvider(),
			new RefusingUpstream(), new EndpointBreaker(new FakeTimeProvider()),
			new CapturingLogger<CapabilityRouter>());
		var memory = new MemoryService(_store, router);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("alpha", "alpha note", "alpha keyword")], []);

		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null);

		res.Retrievers.Degraded.Should().BeTrue();
		res.Retrievers.DegradedReason.Should().Be(SearchDegradedReason.EmbedTransient);
		res.Hits.Select(h => h.Key).Should().Equal("alpha");
	}

	[Fact]
	public async Task IndexFailureWithoutAReason_FallsBackToIndexError()
	{
		// Anything that is not a classified embed failure (SQL error, corrupt file, …) degrades
		// under the generic code rather than a lie.
		var log = new CapturingLogger<SearchService>();
		var svc = new SearchService([new ExplodingIndex()], log);

		var res = await svc.SearchAsync(Proj, "alpha", new SearchFilter(), k: 5);

		res.Retrievers.Degraded.Should().BeTrue();
		res.Retrievers.DegradedReason.Should().Be(SearchDegradedReason.IndexError);
		log.Entries.Should().Contain(e => e.EventId == 400 && e.Level == MsLogLevel.Warning);
	}

	[Fact]
	public async Task NoDegradation_LeavesReasonNull()
	{
		var memory = new MemoryService(_store); // no LLM at all → semantic never attempted, not degraded
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("alpha", "alpha note", "alpha keyword")], []);

		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null);

		res.Retrievers.Degraded.Should().BeFalse();
		res.Retrievers.DegradedReason.Should().BeNull();
	}

	[Fact]
	public async Task DeadLetter_LogsWarningCarryingTheLastExceptionMessage()
	{
		// The dead-letter is the ONE moment an entity leaves the semantic index forever. It used to
		// happen in total silence — the exception that caused it was not even kept.
		var log = new CapturingLogger<AsyncVectorizationWorker>();
		var source = new StaticSource([new SearchDoc(Proj, "notes", "poison", "text")], version: 7);
		var worker = new AsyncVectorizationWorker("vec:notes", source, new ExplodingIndex(),
			new InMemoryIndexCursorStore(), maxAttempts: 1, log: log);

		var r = await worker.DrainAsync();

		r.DeadLettered.Should().Be(1);
		var dead = log.Entries.Should().ContainSingle(e => e.EventId == 401).Which;
		dead.Level.Should().Be(MsLogLevel.Warning);
		dead.Message.Should().Contain("poison").And.Contain(ExplodingIndex.Boom);
		dead.Exception!.Message.Should().Be(ExplodingIndex.Boom);
		// The drain summary carries the numbers a counter would (dead-letters + cursor lag).
		log.Entries.Should().Contain(e => e.EventId == 404 && e.Level == MsLogLevel.Information);
	}

	[Fact]
	public async Task DrainResult_ExposesCursorLag()
	{
		// Lag = source version − cursor: the number that says "semantic search here is behind".
		// A blocked drain holds the cursor at 0 while the source has moved to 7.
		var source = new StaticSource([new SearchDoc(Proj, "notes", "poison", "text")], version: 7);
		var worker = new AsyncVectorizationWorker("vec:notes", source, new ExplodingIndex(),
			new InMemoryIndexCursorStore(), maxAttempts: 5);

		var r = await worker.DrainAsync();

		r.Advanced.Should().BeFalse();
		r.Lag.Should().Be(7);
	}

	// ---- fakes ----

	sealed class EmptyResolver : ILlmRegistryResolver
	{
		public Task<ResolvedRegistry> ResolveAsync(string projectKey, CancellationToken ct = default) =>
			Task.FromResult(new ResolvedRegistry(LlmRegistry.Empty, new Dictionary<string, string>()));
	}

	sealed class FixedResolver(ResolvedRegistry reg) : ILlmRegistryResolver
	{
		public Task<ResolvedRegistry> ResolveAsync(string projectKey, CancellationToken ct = default) => Task.FromResult(reg);
	}

	sealed class UnusedUpstream : IOpenAiCompatibleClient
	{
		public Task<IReadOnlyList<float[]>> EmbedAsync(HttpClient http, string baseUrl, string? apiKey, string model, IReadOnlyList<string> inputs, CancellationToken ct) =>
			throw new NotSupportedException("no route → the chain must never reach an endpoint");
		public Task<IReadOnlyList<RerankHit>> RerankAsync(HttpClient http, string baseUrl, string? apiKey, string model, string query, IReadOnlyList<string> documents, int? topN, CancellationToken ct) =>
			throw new NotSupportedException();
		public Task<string> ChatAsync(HttpClient http, string baseUrl, string? apiKey, string model, IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens, LlmThinking? thinking, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	sealed class RefusingUpstream : IOpenAiCompatibleClient
	{
		public Task<IReadOnlyList<float[]>> EmbedAsync(HttpClient http, string baseUrl, string? apiKey, string model, IReadOnlyList<string> inputs, CancellationToken ct) =>
			throw new LlmUpstreamException(true, "connection refused");
		public Task<IReadOnlyList<RerankHit>> RerankAsync(HttpClient http, string baseUrl, string? apiKey, string model, string query, IReadOnlyList<string> documents, int? topN, CancellationToken ct) =>
			throw new NotSupportedException();
		public Task<string> ChatAsync(HttpClient http, string baseUrl, string? apiKey, string model, IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens, LlmThinking? thinking, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	// An index that fails every read AND write with a plain (unclassified) exception.
	sealed class ExplodingIndex : ISearchIndex
	{
		public const string Boom = "index is on fire";
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;
		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) => throw new InvalidOperationException(Boom);
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) => Task.CompletedTask;
		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) => Task.CompletedTask;
		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) => throw new InvalidOperationException(Boom);
	}

	sealed class StaticSource(IReadOnlyList<SearchDoc> upserts, long version) : ISearchSource
	{
		public Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default) =>
			Task.FromResult(new SourceDelta(upserts, [], version));
	}

	sealed record LogEntry(MsLogLevel Level, int EventId, string Message, Exception? Exception);

	sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<LogEntry> Entries { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception), exception));
	}
}
