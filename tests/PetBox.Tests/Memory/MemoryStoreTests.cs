using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

public sealed class MemoryStoreTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;

	public MemoryStoreTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memstore-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
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

	[Fact]
	public async Task Create_InsertsMeta_AndMaterializesFile()
	{
		await _store.CreateAsync("proj", "notes", "agent notes");
		(await _store.ExistsAsync("proj", "notes")).Should().BeTrue();
		// One file per PROJECT: creating a store is a catalog row, and the project's shared
		// memory file is materialized (name == null, like tasks/sessions).
		File.Exists(ScopedDbFiles.PathFor(_factory.BaseDir, "proj", null)).Should().BeTrue();
	}

	[Fact]
	public async Task Create_SecondStore_SharesTheProjectFile()
	{
		await _store.CreateAsync("proj", "notes", null);
		await _store.CreateAsync("proj", "ops", null);

		Directory.GetFiles(_factory.BaseDir, "*.db").Should().ContainSingle()
			.Which.Should().EndWith("proj.db");
	}

	[Fact]
	public async Task Delete_RemovesMeta_AndTheStoresRows_LeavingSiblingsIntact()
	{
		var memory = new MemoryService(_store);
		await memory.UpsertAsync("proj", "notes", [new MemoryEntryInput { Key = "n1", Type = "Project", Body = "keep" }], []);
		await memory.UpsertAsync("proj", "ops", [new MemoryEntryInput { Key = "o1", Type = "Project", Body = "drop" }], []);

		(await _store.DeleteAsync("proj", "ops")).Should().BeTrue();
		(await _store.ExistsAsync("proj", "ops")).Should().BeFalse();

		// The stores share one file, so a delete drops the store's ROWS — and only those.
		var ctx = _store.GetContext("proj");
		ctx.Entries.Where(e => e.ActiveTo == null).Select(e => e.Store).ToList()
			.Should().BeEquivalentTo(["notes"]);
	}

	[Fact]
	public async Task Create_InvalidName_Throws() =>
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync("proj", "Bad Name", null));

	// Store taxonomy (spec: memoverhaul): a well-known system store name is tagged IsSystem
	// at creation; an ordinary store is not.
	[Fact]
	public async Task Create_SessionDigestsStore_IsMarkedSystem_OrdinaryIsNot()
	{
		await _store.CreateAsync("proj", "session-digests", null);
		await _store.CreateAsync("proj", "notes", null);

		var stores = await _store.ListAsync("proj");
		stores.Single(s => s.Name == "session-digests").IsSystem.Should().BeTrue();
		stores.Single(s => s.Name == "notes").IsSystem.Should().BeFalse();
	}

	// Widening the taxonomy (card ui-memory-system-store-widen): `autocaptured` and `canon`
	// are agent plumbing and must be tagged IsSystem at creation, like session-digests.
	[Theory]
	[InlineData("autocaptured")]
	[InlineData("canon")]
	public async Task Create_CanonAndAutocaptured_AreMarkedSystem(string name)
	{
		await _store.CreateAsync("proj", name, null);

		var stores = await _store.ListAsync("proj");
		stores.Single(s => s.Name == name).IsSystem.Should().BeTrue();
	}

	// SAFETY: IsSystem gates only whole-store deletion, never entry writes. Curating `canon`
	// via the upsert path (memory_upsert) MUST keep working even though the store is IsSystem.
	[Fact]
	public async Task UpsertEntry_IntoSystemStore_Canon_Succeeds()
	{
		var memory = new MemoryService(_store);
		await _store.CreateAsync("proj", "canon", null);
		(await _store.ListAsync("proj")).Single(s => s.Name == "canon").IsSystem.Should().BeTrue();

		var outcome = await memory.UpsertAsync("proj", "canon",
			[new MemoryEntryInput { Key = "index", Type = "reference", Description = "canon index", Body = "pointers" }],
			[]);

		outcome.Result.Applied.Should().BeTrue();
		var ctx = _store.GetContext("proj");
		var active = ctx.Entries.Where(e => e.ActiveTo == null).ToList();
		active.Should().ContainSingle();
		active[0].Body.Should().Be("pointers");
	}

	// atomic:false on memory (spec batch-write-partial-apply). Memory entries carry no
	// intra-batch references, so the cascade degenerates: every entry is independent. What
	// remains is the core promise — valid entries land, refused ones come back with a reason,
	// and a STALE baseline refuses THAT ENTRY, not the call.
	[Fact]
	public async Task Upsert_Partial_ValidEntryLands_InvalidRejectedWithReason()
	{
		var memory = new MemoryService(_store);

		var r = await memory.UpsertAsync("proj", "notes",
			[
				new MemoryEntryInput { Key = "good", Type = "Project", Body = "keep" },
				new MemoryEntryInput { Key = "no-type", Body = "a new entry without a type" },  // type is required on a create
			],
			[], atomic: false);

		r.Result.Applied.Should().BeTrue();
		r.Result.Added.Select(e => e.Key).Should().Equal("good");
		var c = r.Result.Conflicts.Should().ContainSingle().Subject;
		c.Key.Should().Be("no-type");
		c.Kind.Should().Be(TemporalConflictKind.Rejected);
		c.Reason.Should().Contain("type is required");

		var ctx = _store.GetContext("proj");
		ctx.Entries.Where(e => e.ActiveTo == null).Select(e => e.Key).ToList().Should().Equal("good");
	}

	[Fact]
	public async Task Upsert_Partial_StaleEntry_RejectsOnlyItself_SiblingStillLands()
	{
		var memory = new MemoryService(_store);
		await memory.UpsertAsync("proj", "notes", [new MemoryEntryInput { Key = "e", Type = "Project", Body = "v1" }], []);
		await memory.UpsertAsync("proj", "notes", [new MemoryEntryInput { Key = "e", Type = "Project", Body = "v2", Version = 1 }], []);

		// Baseline 1 while the entry is at v2 and its payload MOVED => a genuine Stale.
		var r = await memory.UpsertAsync("proj", "notes",
			[
				new MemoryEntryInput { Key = "e", Type = "Project", Body = "mine", Version = 1 },
				new MemoryEntryInput { Key = "fresh", Type = "Project", Body = "new" },
			],
			[], atomic: false);

		r.Result.Applied.Should().BeTrue();
		r.Result.Added.Select(e => e.Key).Should().Equal("fresh");        // the clean entry landed
		r.Result.Conflicts.Should().ContainSingle()
			.Which.Kind.Should().Be(TemporalConflictKind.Stale);          // the stale one refused ITSELF
		var ctx = _store.GetContext("proj");
		ctx.Entries.Single(e => e.ActiveTo == null && e.Key == "e").Body.Should().Be("v2"); // never clobbered
	}

	// The default is untouched: one bad entry still aborts the whole batch.
	[Fact]
	public async Task Upsert_WithoutTheFlag_OneInvalidEntry_AbortsTheWholeBatch()
	{
		var memory = new MemoryService(_store);

		var act = () => memory.UpsertAsync("proj", "notes",
			[
				new MemoryEntryInput { Key = "good", Type = "Project", Body = "keep" },
				new MemoryEntryInput { Key = "no-type", Body = "x" },
			],
			[]);

		await act.Should().ThrowAsync<ArgumentException>();
		var ctx = _store.GetContext("proj");
		ctx.Entries.Count(e => e.ActiveTo == null).Should().Be(0); // not even `good`
	}

	[Fact]
	public async Task MemoryEntry_TemporalRoundtrip_ThroughStoreFile()
	{
		await _store.CreateAsync("proj", "notes", null);
		var ctx = _store.GetContext("proj");

		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new MemoryEntry { Key = "go-style", Version = 0, Type = MemoryType.Reference, Description = "Go conventions", Body = "tabs", Tags = "go,style" },
		});
		r.Applied.Should().BeTrue();

		var active = ctx.Entries.Where(e => e.ActiveTo == null).ToList();
		active.Should().ContainSingle();
		active[0].Tags.Should().Be("go,style");
		active[0].Type.Should().Be(MemoryType.Reference);
	}
}
