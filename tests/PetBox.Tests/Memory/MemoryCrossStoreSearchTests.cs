using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

// The point of the per-project memory file: a project's stores share ONE search_fts and ONE
// search_vec (a store is the entity Type), so the sweep across stores is a single query per
// retriever leg with a `Type IN (…)` narrowing — not N searches stitched together. These tests pin
// the BEHAVIOUR that must survive that change: the sweep still finds a hit in any store, an
// explicit store filter still narrows, stores stay isolated (same key in two stores is two
// entries), and a store's entries vanish from the shared index when the store is deleted.
public sealed class MemoryCrossStoreSearchTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemoryCrossStoreSearchTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memxstore-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db, _factory);
		_memory = new MemoryService(_store);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	Task Write(string store, string key, string body) =>
		_memory.UpsertAsync(Proj, store, [new MemoryEntryInput { Key = key, Type = "Project", Description = key, Body = body }], []);

	static SearchRequest<MemoryEntryFilter, MemorySortBy> Query(string? q, string? store = null, int limit = 20) =>
		new() { Query = q, Filter = new MemoryEntryFilter(Store: store), Limit = limit };

	[Fact]
	public async Task Sweep_FindsHitsInEveryStore_InOneQuery()
	{
		await Write("notes", "n1", "the deployment pipeline runs nightly");
		await Write("decisions", "d1", "we chose sqlite for the deployment store");
		await Write("scratch", "s1", "unrelated content");

		var hits = await _memory.SearchEntriesAsync(Proj, Query("deployment"));

		hits.Hits.Select(h => h.Store + "/" + h.Entry.Key).Should().BeEquivalentTo(["notes/n1", "decisions/d1"]);
		hits.Retrievers!.Value.Lexical.Should().BeTrue();
	}

	[Fact]
	public async Task StoreFilter_NarrowsToThatStore()
	{
		await Write("notes", "n1", "the deployment pipeline runs nightly");
		await Write("decisions", "d1", "we chose sqlite for the deployment store");

		var hits = await _memory.SearchEntriesAsync(Proj, Query("deployment", store: "decisions"));

		hits.Hits.Should().ContainSingle();
		hits.Hits[0].Store.Should().Be("decisions");
		hits.Hits[0].Entry.Key.Should().Be("d1");
	}

	[Fact]
	public async Task Sweep_SkipsTheSweepExcludedStores_ButAnExplicitFilterReachesThem()
	{
		await Write("notes", "n1", "deployment notes");
		await Write("ops", "o1", "deployment secrets");

		// `ops` is sensitive: never in the implicit sweep…
		(await _memory.SearchEntriesAsync(Proj, Query("deployment"))).Hits
			.Select(h => h.Store).Should().BeEquivalentTo(["notes"]);
		// …but an explicit store filter still reaches it.
		(await _memory.SearchEntriesAsync(Proj, Query("deployment", store: "ops"))).Hits
			.Select(h => h.Store).Should().BeEquivalentTo(["ops"]);
	}

	[Fact]
	public async Task SameKeyInTwoStores_AreTwoIndependentEntries()
	{
		// Keys are unique only WITHIN a store — the shared file must not collapse them (the PK is
		// (Store, Key, Version) and the search address is (Type=store, Id=key)).
		await Write("notes", "index", "notes index body deployment");
		await Write("canon", "index", "canon index body deployment");

		var hits = await _memory.SearchEntriesAsync(Proj, Query("deployment"));
		hits.Hits.Select(h => h.Store + "/" + h.Entry.Key).Should().BeEquivalentTo(["notes/index", "canon/index"]);

		(await _memory.GetAsync(Proj, "notes", "index"))!.Body.Should().Be("notes index body deployment");
		(await _memory.GetAsync(Proj, "canon", "index"))!.Body.Should().Be("canon index body deployment");
	}

	[Fact]
	public async Task Stores_HaveIndependentVersionCursors()
	{
		await Write("notes", "n1", "one");
		await Write("notes", "n2", "two");
		var ops = await _memory.UpsertAsync(Proj, "ops",
			[new MemoryEntryInput { Key = "o1", Type = "Project", Body = "first in ops" }], []);

		// The store is a temporal PARTITION: `ops` starts its own version space at 1, unaffected by
		// the two revisions already written to `notes` in the same file.
		ops.Result.CurrentVersion.Should().Be(1);
	}

	[Fact]
	public async Task DeleteStore_PurgesItsDocsFromTheSharedIndex()
	{
		await Write("notes", "n1", "deployment notes");
		await Write("scratch", "s1", "deployment scratch");

		(await _memory.DeleteStoreAsync(Proj, "scratch")).Should().BeTrue();

		// The sibling store is untouched, and the dropped store leaves no orphan lexical doc behind
		// in the file they share.
		var hits = await _memory.SearchEntriesAsync(Proj, Query("deployment"));
		hits.Hits.Select(h => h.Store).Should().BeEquivalentTo(["notes"]);

		using var ctx = _store.GetContext(Proj);
		ctx.Execute<long>("SELECT count(*) FROM search_fts WHERE Type = 'scratch'").Should().Be(0);
	}

	[Fact]
	public async Task Listing_WithoutQuery_SweepsEveryStore()
	{
		await Write("notes", "n1", "one");
		await Write("decisions", "d1", "two");

		var hits = await _memory.SearchEntriesAsync(Proj, Query(null));

		hits.Hits.Select(h => h.Store + "/" + h.Entry.Key).Should().BeEquivalentTo(["notes/n1", "decisions/d1"]);
		hits.Retrievers.Should().BeNull(); // a listing ran no retriever
	}
}
