using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

// spec ui-list-pagination: the memory-store detail page pages the active entries
// server-side (OFFSET/LIMIT, ordered by Key) and filters by a substring over
// Key/Description/Body/Tags — so a 200-entry store (dead-tail keys included) is reachable
// by paging rather than dumped whole or hidden in a title attribute.
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
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db.Factory(), _factory);
		_memory = new MemoryService(_store, llm: null);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	async Task Seed(int count)
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes",
			Enumerable.Range(1, count).Select(i => new MemoryEntryInput
			{
				Key = $"k{i:000}",
				Version = 0,
				Type = "Project",
				Description = $"entry number {i}",
				Body = $"body {i}" + (i == count ? " needle" : ""),
			}).ToList(), []);
	}

	[Fact]
	public async Task ListPage_AppliesOffset_AndReportsHasNext()
	{
		await Seed(95);

		var p0 = await _memory.ListActiveEntriesPageAsync(Proj, "notes", null, pageNum: 0, pageSize: 40);
		p0.Total.Should().Be(95);
		p0.HasNext.Should().BeTrue();
		p0.Entries.Count.Should().Be(40);
		p0.Entries[0].Key.Should().Be("k001");

		var p1 = await _memory.ListActiveEntriesPageAsync(Proj, "notes", null, pageNum: 1, pageSize: 40);
		p1.HasNext.Should().BeTrue();
		p1.Entries[0].Key.Should().Be("k041"); // OFFSET 40 → a different slice
		p1.Entries.Select(e => e.Key).Should().NotContain("k001");

		// The dead tail (k081..k095) is reachable by paging, not stuck in a summary.
		var p2 = await _memory.ListActiveEntriesPageAsync(Proj, "notes", null, pageNum: 2, pageSize: 40);
		p2.HasNext.Should().BeFalse();
		p2.Entries.Count.Should().Be(15);
		p2.Entries.Select(e => e.Key).Should().Contain("k095");
	}

	[Fact]
	public async Task ListPage_Search_NarrowsOverBodyAndKey()
	{
		await Seed(95);

		// Body substring: only the last entry carries "needle".
		var byBody = await _memory.ListActiveEntriesPageAsync(Proj, "notes", "needle", pageNum: 0, pageSize: 40);
		byBody.Total.Should().Be(1);
		byBody.Entries.Single().Key.Should().Be("k095");

		// Key substring narrows too and never loads the whole set.
		var byKey = await _memory.ListActiveEntriesPageAsync(Proj, "notes", "k012", pageNum: 0, pageSize: 40);
		byKey.Total.Should().Be(1);
		byKey.Entries.Single().Key.Should().Be("k012");
	}
}
