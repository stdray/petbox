using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

// spec listing-tail-reachable: the memory-store detail page's listing mode no longer offset-pages
// (MemoryService.ListActiveEntriesPageAsync / FindActiveEntryPageAsync are GONE — an offset
// silently re-serves/swallows rows under concurrent writes, KeysetCursor.cs:17-23). The listing
// now runs through the SAME uniform read every other mode uses — SearchEntriesAsync(Query: null,
// Limit: 0) — which returns the FULL deterministic order unbounded; the UI adapter
// (MemoryStoreModel) is the one that seeks a KeysetCursor through it and slices a page (mirrors
// TasksTools.SearchAsync's own listing mode). These tests cover what the SERVICE promises that
// adapter: the unbounded order is complete, deterministic, and carries the Created/Updated values
// a cursor needs. The adapter-level keyset behavior itself (cursor resume across pages, fingerprint
// mismatch, deep-link seeking) is covered end-to-end in MemoryStoreKeysetPagingTests.
public sealed class MemoryStorePagingTests : IDisposable
{
	const string Proj = "proj";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemoryStorePagingTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mempage-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_store = new MemoryStore(_db.Factory(), _factory);
		_memory = new MemoryService(_store, llm: null);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	async Task Seed(int count, string type = "Project")
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes",
			Enumerable.Range(1, count).Select(i => new MemoryEntryInput
			{
				Key = $"k{i:000}",
				Version = 0,
				Type = type,
				Description = $"entry number {i}",
				Body = $"body {i}",
			}).ToList(), []);
	}

	// Limit: 0 in listing mode (Query: null) is UNBOUNDED — the whole active set, not the "no cap"
	// of a query's candidate pool. A 200+-entry store must come back whole in one call; the
	// adapter, not the service, is the one that slices a page out of it.
	[Fact]
	public async Task ListingMode_LimitZero_ReturnsTheWholeActiveSet_NeverTruncated()
	{
		await Seed(227);

		var res = await _memory.SearchEntriesAsync(Proj, new SearchRequest<MemoryEntryFilter, MemorySortBy>
		{
			Query = null,
			Filter = new MemoryEntryFilter("notes"),
			Limit = 0,
		});

		res.Hits.Count.Should().Be(227);
		res.Hits.Select(h => h.Entry.Key).Distinct().Count().Should().Be(227, "every key must appear exactly once — no row skipped, none duplicated");
		res.Retrievers.Should().BeNull("a listing runs no relevance leg");
	}

	// The default listing order (Updated desc, then Key, then Store — MemoryService.SortSelected)
	// is a TOTAL order: two entries upserted in the same call still tie-break deterministically on
	// Key, which is exactly what a keyset cursor needs to never land on an ambiguous boundary.
	[Fact]
	public async Task ListingMode_DefaultOrder_IsUpdatedDescThenKey_ATotalOrder()
	{
		await Seed(5);
		// A later, separate upsert bumps k003's Updated past the rest.
		await _memory.UpsertAsync(Proj, "notes",
			[new MemoryEntryInput { Key = "k003", Version = 1, Type = "Project", Description = "bumped", Body = "b" }], []);

		var res = await _memory.SearchEntriesAsync(Proj, new SearchRequest<MemoryEntryFilter, MemorySortBy>
		{
			Query = null,
			Filter = new MemoryEntryFilter("notes"),
			Limit = 0,
		});

		res.Hits[0].Entry.Key.Should().Be("k003", "the most recently updated entry sorts first by default");
		// The remaining four, never touched again, tie-break on Key ascending.
		res.Hits.Skip(1).Select(h => h.Entry.Key).Should().BeInAscendingOrder(StringComparer.Ordinal);
	}

	// MemoryEntryHit carries Created/Updated (spec listing-tail-reachable) precisely so a caller
	// building a KeysetCursor's sort-key value doesn't need a second round-trip to the raw
	// MemoryEntry — MemoryEntryView (the wire-facing projection) deliberately omits them.
	[Fact]
	public async Task SearchEntriesAsync_Hits_CarryCreatedAndUpdated()
	{
		await Seed(3);

		var res = await _memory.SearchEntriesAsync(Proj, new SearchRequest<MemoryEntryFilter, MemorySortBy>
		{
			Query = null,
			Filter = new MemoryEntryFilter("notes"),
			Limit = 0,
		});

		foreach (var hit in res.Hits)
		{
			hit.Created.Should().NotBe(default(DateTime));
			hit.Updated.Should().NotBe(default(DateTime));
		}
	}

	// Filter.Type now applies to LISTING too (Query: null) — this is the very inconsistency the
	// card closed: the old bespoke ListActiveEntriesPageAsync never took a type filter at all.
	[Fact]
	public async Task ListingMode_TypeFilter_Narrows()
	{
		await Seed(3, type: "Project");
		await _memory.UpsertAsync(Proj, "notes",
			[new MemoryEntryInput { Key = "f001", Version = 0, Type = "Feedback", Description = "fb", Body = "b" }], []);

		var res = await _memory.SearchEntriesAsync(Proj, new SearchRequest<MemoryEntryFilter, MemorySortBy>
		{
			Query = null,
			Filter = new MemoryEntryFilter("notes", "Feedback"),
			Limit = 0,
		});

		res.Hits.Should().ContainSingle().Which.Entry.Key.Should().Be("f001");
	}
}
