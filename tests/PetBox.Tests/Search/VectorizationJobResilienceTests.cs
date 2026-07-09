using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Tests.Memory;
using PetBox.Web.Search;

namespace PetBox.Tests.Search;

// The vectorization drain walks store FILES and opens them with raw NewConnection()s, which skip
// _ensureSchema — so it must (a) run migrations itself before draining a file (a store last opened
// pre-M006 has no search_cursor and killed the whole pass in prod), and (b) isolate per-file
// failures so one broken store cannot block the backfill of every other store (spec:
// durable-backfill).
public sealed class VectorizationJobResilienceTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;

	public VectorizationJobResilienceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-vecjob-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		try { Directory.Delete(_dir, recursive: true); } catch { }
	}

	ScopedDbFactory<MemoryDb> NewFactory() =>
		new(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);

	async Task SeedStoreAsync(ScopedDbFactory<MemoryDb> factory, string store)
	{
		var memory = new MemoryService(new MemoryStore(_db, factory));
		var r = await memory.UpsertAsync(Proj, store,
			new[] { new MemoryEntryInput { Key = "k1", Type = "Project", Body = "some body text" } },
			Array.Empty<MemoryDelete>());
		Assert.True(r.Result.Applied);
	}

	[Fact]
	public async Task Drain_migrates_a_store_file_left_behind_by_an_old_schema()
	{
		var factory = NewFactory();
		await SeedStoreAsync(factory, "a");

		// Simulate a file untouched since before M006: search tables gone, journal row gone —
		// exactly what NewConnection() (no _ensureSchema) sees on such a file.
		using (var raw = factory.NewEnsuredConnection(Proj, "a"))
		{
			raw.Execute("DROP TABLE search_cursor");
			raw.Execute("DELETE FROM VersionInfo WHERE Version = 6");
		}

		// Fresh factory = fresh process (GetDb's ensure cache is per-instance).
		var job = new MemoryVectorizationJob(NewFactory(), new FakeLlmClient());
		await job.DrainAllAsync(CancellationToken.None); // must not throw

		using var check = factory.NewEnsuredConnection(Proj, "a");
		Assert.Equal(1, check.Execute<int>(
			"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='search_cursor'"));
	}

	[Fact]
	public async Task Drain_skips_a_broken_store_and_still_drains_the_rest()
	{
		var factory = NewFactory();
		await SeedStoreAsync(factory, "a");
		await SeedStoreAsync(factory, "b");

		// Break store "a" irreparably (migrations consider the file current, so ensure can't heal
		// it): the drain on "a" throws and must be contained to that store.
		using (var raw = factory.NewEnsuredConnection(Proj, "a"))
			raw.Execute("DROP TABLE memory_entries");

		var job = new MemoryVectorizationJob(NewFactory(), new FakeLlmClient());
		await job.DrainAllAsync(CancellationToken.None); // must not throw

		// The healthy store's cursor advanced — its drain ran despite "a" failing first.
		using var check = factory.NewEnsuredConnection(Proj, "b");
		Assert.True(check.Execute<long>("SELECT coalesce(max(Version), 0) FROM search_cursor") > 0);
	}
}
