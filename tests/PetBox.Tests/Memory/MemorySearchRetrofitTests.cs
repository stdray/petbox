using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

// The retrofit invariants behind the search contract: the lexical floor is transactional (rolls
// back WITH the entity), pre-retrofit data backfills lexically on demand, and vectors are
// materialized off the write path by the async-vectorization worker — durably (a down embedder
// backfills on recovery, nothing lost).
public sealed class MemorySearchRetrofitTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;

	public MemorySearchRetrofitTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memretro-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db.Factory(), _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static MemoryEntryInput Entry(string key, string description, string body) =>
		new() { Key = key, Version = 0, Type = "Project", Description = description, Body = body };

	async Task<DrainResult> DrainVectors(ILlmClient llm, string store)
	{
		DataConnection Connect() => _factory.NewEnsuredConnection(Proj);
		var target = new VectorSearchIndex(Connect, new LlmClientEmbedder(llm, Proj));
		var source = new MemorySearchSource(Connect, Proj, store);
		var cursor = new SqliteIndexCursorStore(Connect);
		var worker = new AsyncVectorizationWorker(MemoryCursors.Vector(store), source, target, cursor);
		return await worker.DrainAsync();
	}

	[Fact]
	public async Task LexicalFloor_IsTransactional_RollbackLeavesNoTrace()
	{
		var ctx = _factory.GetDb(Proj); // auto-creates the file + schema (incl. search_fts)
		var fts = new SqliteFtsIndex(() => ctx);
		var entry = new MemoryEntry { Key = "k1", Type = MemoryType.Project, Description = "alpha", Body = "alpha body" };

		// onWithinTx writes the FTS row INSIDE the entity tx, then throws → both must roll back.
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			TemporalStore.UpsertAsync(ctx, new[] { entry }, [], 0,
				onWithinTx: async (tx, upserted, _, c) =>
				{
					foreach (var e in upserted)
						await fts.IndexAsync(tx, MemorySearchDocs.ToDoc(e, Proj), c);
					throw new InvalidOperationException("boom");
				}));

		ctx.GetTable<MemoryEntry>().Count(e => e.ActiveTo == null).Should().Be(0); // entity rolled back
		ctx.Execute<long>("SELECT count(*) FROM search_fts").Should().Be(0);        // and so did the FTS write
	}

	[Fact]
	public async Task Lexical_BackfillsForPreRetrofitEntries()
	{
		var memory = new MemoryService(_store, llm: null);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("k1", "alpha note", "alpha keyword body")], []);

		// Simulate a store written before the retrofit: entries exist but search_fts is empty.
		var ctx = _store.GetContext(Proj);
		ctx.Execute("DELETE FROM search_fts");

		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null);

		res.Hits.Select(h => h.Key).Should().Contain("k1");                          // found after on-demand backfill
		ctx.Execute<long>("SELECT count(*) FROM search_fts").Should().BeGreaterThan(0); // and the index was rebuilt
	}

	[Fact]
	public async Task Worker_MaterializesVectors_AndAdvancesCursor()
	{
		var llm = new FakeLlmClient();
		var memory = new MemoryService(_store, llm);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("k1", "alpha note", "alpha body")], []);

		var ctx = _store.GetContext(Proj);
		ctx.Execute<long>("SELECT count(*) FROM search_vec").Should().Be(0); // write path never embeds

		var r = await DrainVectors(llm, "notes");
		r.Indexed.Should().Be(1);
		r.Advanced.Should().BeTrue();
		r.Cursor.Should().BeGreaterThan(0);
		ctx.Execute<long>("SELECT count(*) FROM search_vec").Should().Be(1);

		var again = await DrainVectors(llm, "notes"); // nothing new past the cursor
		again.Indexed.Should().Be(0);
	}

	[Fact]
	public async Task Worker_DurableBackfill_HoldsCursorThenRecovers()
	{
		var working = new FakeLlmClient();
		var memory = new MemoryService(_store, working);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("k1", "alpha note", "alpha body")], []);
		var ctx = _store.GetContext(Proj);

		// Embedder down: the item fails transiently, the cursor is held (not advanced), nothing lost.
		var down = await DrainVectors(new ThrowingLlmClient(), "notes");
		down.Indexed.Should().Be(0);
		down.Advanced.Should().BeFalse();
		ctx.Execute<long>("SELECT count(*) FROM search_vec").Should().Be(0);

		// Recovery: the held cursor re-drains the same delta and backfills the vector.
		var recovered = await DrainVectors(working, "notes");
		recovered.Indexed.Should().Be(1);
		recovered.Advanced.Should().BeTrue();
		ctx.Execute<long>("SELECT count(*) FROM search_vec").Should().Be(1);
	}
}
