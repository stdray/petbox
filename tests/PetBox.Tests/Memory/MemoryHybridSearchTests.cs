using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Search;
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

	// Vectors are now materialized OFF the write path by the async-vectorization worker, so a test
	// that needs the semantic leg must drain first (with the SAME embedder the query path uses, so
	// the model/dim guard matches). Mirrors MemoryVectorizationJob for one store.
	async Task<DrainResult> DrainVectors(ILlmClient llm, string store)
	{
		DataConnection Connect() => _factory.NewConnection(Proj, store);
		var target = new VectorSearchIndex(Connect, new LlmClientEmbedder(llm, Proj));
		var source = new MemorySearchSource(Connect, Proj);
		var cursor = new SqliteIndexCursorStore(Connect);
		var worker = new AsyncVectorizationWorker(MemorySearchDocs.VectorIndex, source, target, cursor);
		return await worker.DrainAsync();
	}

	[Fact]
	public async Task Hybrid_FusesLexicalAndSemanticUnion_AndReportsBothRan()
	{
		var llm = new FakeLlmClient();
		var memory = new MemoryService(_store, llm);
		await memory.CreateStoreAsync(Proj, "notes", null);
		// "alpha" entry hits lexically on the query token; "beta" does NOT contain the token
		// but its embedding is steered to sit near the query vector, so only semantic finds it.
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("alpha", "alpha note", "the alpha keyword appears here"),
			Entry("beta", "beta note", FakeLlmClient.NearQueryMarker + " unrelated words"),
		], []);
		await DrainVectors(llm, "notes"); // materialize the Class-B vectors the semantic leg needs

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
		// Embedder that throws: the write never embeds (Class-B is off the write path), so the
		// upsert succeeds regardless; at query time the semantic leg fails → lexical-only,
		// flagged degraded.
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
		var llm = new FakeLlmClient();
		var memory = new MemoryService(_store, llm);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("good", "good note", FakeLlmClient.NearQueryMarker + " body"),
			Entry("bad", "bad note", FakeLlmClient.NearQueryMarker + " body"),
		], []);
		await DrainVectors(llm, "notes");

		// Corrupt "bad"'s stored vector to a different model — the query embedding's (model,dim)
		// guard must exclude it from the semantic candidate set.
		var ctx = _store.GetContext(Proj, "notes");
		ctx.Execute("UPDATE search_vec SET Model = 'other-model' WHERE Id = 'bad'");

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
}
