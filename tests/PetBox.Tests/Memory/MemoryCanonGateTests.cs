using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

// The canon write-gate (spec agent-wiring, memory-canon-storage): the "canon" store is a curated
// index pulled into every agent session, so an entry body over the 10000-char budget is rejected
// with an educational message. Gated in the service door so both memory_upsert and memory_remember
// (which share IMemoryService.UpsertAsync) are covered; other stores are never touched.
[Collection("DataModule")]
public sealed class MemoryCanonGateTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemoryCanonGateTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memcanon-" + Guid.NewGuid().ToString("N"));
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
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	static MemoryEntryInput Index(string body) => new()
	{
		Key = "index",
		Version = 0,
		Type = "Reference",
		Description = "canon index",
		Body = body,
	};

	[Fact]
	public async Task Canon_OversizedBody_IsRejected_WithEducationalMessage()
	{
		var huge = new string('x', 10001);
		var act = async () => await _memory.UpsertAsync(Proj, "canon", new[] { Index(huge) }, []);

		var ex = await act.Should().ThrowAsync<ArgumentException>();
		ex.Which.Message.Should().Contain("budget");
		ex.Which.Message.Should().Contain("COMPACT INDEX");
		ex.Which.Message.Should().Contain("pointers");
	}

	[Fact]
	public async Task Canon_SmallBody_IsAccepted()
	{
		var r = await _memory.UpsertAsync(Proj, "canon", new[] { Index("a compact index of pointers") }, []);
		r.Result.Applied.Should().BeTrue();

		var entry = await _memory.GetAsync(Proj, "canon", "index");
		entry.Should().NotBeNull();
		entry!.Body.Should().Contain("compact index");
	}

	[Fact]
	public async Task NonCanonStore_LargeBody_IsUnaffected()
	{
		// The gate is scoped to the canon store: a large body in an ordinary store applies fine.
		var huge = new string('y', 12000);
		var input = new MemoryEntryInput { Key = "big", Version = 0, Type = "Project", Body = huge };
		var r = await _memory.UpsertAsync(Proj, "notes", new[] { input }, []);
		r.Result.Applied.Should().BeTrue();
	}
}
