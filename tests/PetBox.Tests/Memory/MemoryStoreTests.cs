using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;

namespace PetBox.Tests.Memory;

[Collection("DataModule")]
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
		_store = new MemoryStore(_db, _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Create_InsertsMeta_AndMaterializesFile()
	{
		await _store.CreateAsync("proj", "notes", "agent notes");
		(await _store.ExistsAsync("proj", "notes")).Should().BeTrue();
		File.Exists(ScopedDbFiles.PathFor(_factory.BaseDir, "proj", "notes")).Should().BeTrue();
	}

	[Fact]
	public async Task Delete_RemovesMeta()
	{
		await _store.CreateAsync("proj", "notes", null);
		(await _store.DeleteAsync("proj", "notes")).Should().BeTrue();
		(await _store.ExistsAsync("proj", "notes")).Should().BeFalse();
		// Physical file removal is best-effort (TryDelete bails on a Windows lock and
		// orphan-cleanup retries later); DeleteAsync only contracts that the metadata
		// is gone. Mirrors LogStore's delete coverage — see EntityToolsTests.
	}

	[Fact]
	public async Task Create_InvalidName_Throws() =>
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync("proj", "Bad Name", null));

	[Fact]
	public async Task MemoryEntry_TemporalRoundtrip_ThroughStoreFile()
	{
		await _store.CreateAsync("proj", "notes", null);
		var ctx = _store.GetContext("proj", "notes");

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
