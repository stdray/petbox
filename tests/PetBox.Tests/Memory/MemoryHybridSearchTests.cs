using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

// Hybrid (lexical FTS ⊕ semantic vectors, RRF-fused) memory search and its honest
// provenance: a deterministic fake embedder makes the semantic leg reproducible so we can
// assert (a) the fused union, (b) graceful degrade to lexical-only when embedding is
// absent/failing, and (c) the model/dim guard that ignores incomparable stored vectors.
[Collection("DataModule")]
public sealed class MemoryHybridSearchTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;

	public MemoryHybridSearchTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memhybrid-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
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
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	static MemoryEntryInput Entry(string key, string description, string body) =>
		new() { Key = key, Version = 0, Type = "Project", Description = description, Body = body };

	[Fact]
	public async Task Hybrid_FusesLexicalAndSemanticUnion_AndReportsBothRan()
	{
		var memory = new MemoryService(_store, new FakeLlmClient());
		await memory.CreateStoreAsync(Proj, "notes", null);
		// "alpha" entry hits lexically on the query token; "beta" does NOT contain the token
		// but its embedding is steered to sit near the query vector, so only semantic finds it.
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("alpha", "alpha note", "the alpha keyword appears here"),
			Entry("beta", "beta note", FakeLlmClient.NearQueryMarker + " unrelated words"),
		], []);

		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null);

		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeTrue();
		res.Retrievers.Degraded.Should().BeFalse();
		// Union of both retrievers: lexical found alpha, semantic found beta.
		res.Hits.Select(h => h.Key).Should().BeEquivalentTo(["alpha", "beta"]);
	}

	[Fact]
	public async Task NoLlm_DegradesToLexicalOnly()
	{
		// No embedder wired at all.
		var memory = new MemoryService(_store, llm: null);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("alpha", "alpha note", "alpha keyword")], []);

		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null);

		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse();
		// _llm is null → semantic was never attempted, so this is not "degraded".
		res.Retrievers.Degraded.Should().BeFalse();
		res.Hits.Select(h => h.Key).Should().Equal("alpha");
	}

	[Fact]
	public async Task ThrowingEmbedder_AtQueryTime_DegradesAndFlags()
	{
		// Embedder that throws: write still succeeds (embed-on-write swallows), and at query
		// time the semantic leg fails → lexical-only result flagged degraded.
		var memory = new MemoryService(_store, new ThrowingLlmClient());
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("alpha", "alpha note", "alpha keyword")], []);

		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null);

		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse();
		res.Retrievers.Degraded.Should().BeTrue();
		res.Hits.Select(h => h.Key).Should().Equal("alpha");
	}

	[Fact]
	public async Task SemanticOnly_WithModelDimMismatchRow_IgnoresIncomparableVector()
	{
		var memory = new MemoryService(_store, new FakeLlmClient());
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("good", "good note", FakeLlmClient.NearQueryMarker + " body"),
			Entry("bad", "bad note", FakeLlmClient.NearQueryMarker + " body"),
		], []);

		// Corrupt "bad"'s stored vector to a different model/dim — the query embedding's
		// (model,dim) guard must exclude it from the semantic candidate set.
		var ctx = _store.GetContext(Proj, "notes");
		ctx.MemoryVec.Where(v => v.Key == "bad").Set(v => v.Model, "other-model").Update();

		// Lexical off so only the semantic leg drives the result set.
		var res = await memory.SearchAsync(Proj, "notes", "anything", type: null, lexical: false, semantic: true);

		res.Retrievers.Lexical.Should().BeFalse();
		res.Retrievers.Semantic.Should().BeTrue();
		res.Hits.Select(h => h.Key).Should().Equal("good"); // "bad" guarded out
	}

	[Fact]
	public async Task Lexical_MatchesCyrillicQuery()
	{
		// User content is partly Russian — a Cyrillic query MUST tokenize and match. An
		// ASCII-only ([a-z0-9]) query tokenizer would drop the Cyrillic query entirely and
		// degrade to a plain listing; the Unicode-aware tokenizer + unicode61 FTS must hit.
		var memory = new MemoryService(_store, llm: null);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("ru", "заметка про деплой", "разворачиваем сервер и настраиваем прокси"),
			Entry("en", "english note", "deploy the server"),
		], []);

		var res = await memory.SearchAsync(Proj, "notes", "сервер", type: null);

		res.Retrievers.Lexical.Should().BeTrue();
		res.Hits.Select(h => h.Key).Should().Equal("ru"); // only the Cyrillic doc matches "сервер*"
	}

	// ---- deterministic fakes ----

	// Fixed-dim embedder: vector derived from a stable text hash, so the same text always
	// embeds to the same point. A sentinel marker (NearQueryMarker) steers a document's
	// embedding toward the query vector so semantic-only hits are reproducible.
	sealed class FakeLlmClient : ILlmClient
	{
		public const int Dim = 8;
		public const string Model = "fake-embed-v1";
		public const string NearQueryMarker = "__NEARQUERY__";

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default)
		{
			var vectors = request.Inputs.Select(Vector).ToList();
			return Task.FromResult(new EmbedResult(vectors, new ModelIdentity(Model, Dim),
				new ServedBy("fake", Model, 1, Degraded: false)));
		}

		static float[] Vector(string text)
		{
			// Any text carrying the marker (and any query) collapses to the same unit vector,
			// so marked documents sit adjacent to the query embedding.
			if (text.Contains(NearQueryMarker) || !text.Contains(' ') || IsQueryLike(text))
			{
				var q = new float[Dim];
				q[0] = 1f;
				return q;
			}
			var v = new float[Dim];
			var h = unchecked((uint)text.GetHashCode());
			for (var i = 0; i < Dim; i++)
			{
				v[i] = ((h >> i) & 1) == 1 ? 1f : -1f;
				h = h * 2654435761u + 1u;
			}
			return v;
		}

		// Heuristic: short, single-token inputs are treated as queries and map to the
		// query vector — keeps the semantic leg deterministic for the test queries used.
		static bool IsQueryLike(string text) => !text.Contains('\n') && text.Split(' ').Length <= 2;

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}

	// Embedder whose every call throws — exercises the degrade paths (embed-on-write must
	// swallow it; query-time semantic must catch and flag degraded).
	sealed class ThrowingLlmClient : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new InvalidOperationException("embed down");
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}
}
