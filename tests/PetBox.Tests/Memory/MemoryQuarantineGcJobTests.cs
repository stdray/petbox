using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Search;

namespace PetBox.Tests.Memory;

// Quarantine self-cleaning (spec: memoverhaul): the GC retires aged autocaptured facts that
// never earned a DELIBERATE reach. report-only writes nothing (only logs candidates); enforce
// soft-deletes recoverably; it touches ONLY the autocaptured store, and spares any entry that
// was deliberately searched/opened or is still young.
[Collection("DataModule")]
public sealed class MemoryQuarantineGcJobTests : IDisposable
{
	const string Proj = "proj";
	const string Store = SessionFactsJob.Store; // "autocaptured"

	// Negative min-age → cutoff in the future → every existing entry counts as "old enough".
	static readonly TimeSpan AllOld = TimeSpan.FromMinutes(-5);
	static readonly TimeSpan NoThrottle = TimeSpan.Zero;

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;
	readonly MemoryUsageRecorder _recorder;

	public MemoryQuarantineGcJobTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memgc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db, _factory);
		_memory = new MemoryService(_store, llm: null);
		_recorder = new MemoryUsageRecorder(_factory);
	}

	public void Dispose()
	{
		_recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	MemoryQuarantineGcJob Job(bool enforce, TimeSpan? minAge = null) =>
		new(_factory, _memory, logger: null, minAge: minAge ?? AllOld, enforce: enforce, scanInterval: NoThrottle);

	async Task Seed(string store, params string[] keys) =>
		await _memory.UpsertAsync(Proj, store,
			keys.Select(k => new MemoryEntryInput
			{
				Key = k,
				Version = 0,
				Type = "Project",
				Description = $"autocaptured fact {k}",
				Body = $"body {k}",
			}).ToList(), []);

	[Fact]
	public async Task ReportOnly_WritesNothing_EntryStaysActive()
	{
		await Seed(Store, "a1", "a2");

		var retired = await Job(enforce: false).DrainAllAsync(CancellationToken.None);

		retired.Should().Be(0); // report-only never acts
		(await _memory.GetAsync(Proj, Store, "a1")).Should().NotBeNull();
		(await _memory.GetAsync(Proj, Store, "a2")).Should().NotBeNull();
	}

	[Fact]
	public async Task Enforce_SoftDeletesUnprovenEntries_Recoverably()
	{
		await Seed(Store, "a1", "a2");

		var retired = await Job(enforce: true).DrainAllAsync(CancellationToken.None);

		retired.Should().Be(2);
		(await _memory.GetAsync(Proj, Store, "a1")).Should().BeNull(); // no longer active
		(await _memory.GetAsync(Proj, Store, "a2")).Should().BeNull();
		// History is kept: the closed revision is still in the delta from cursor 0.
		var delta = await _memory.DeltaAsync(Proj, Store, 0);
		delta.Result.Removed.Should().Contain("a1").And.Contain("a2");
	}

	[Fact]
	public async Task Enforce_SparesDeliberatelyReachedEntry()
	{
		await Seed(Store, "kept", "dead");
		_recorder.Surfaced(Proj, Store, ["kept"], deliberate: true); // proven value
		_recorder.Surfaced(Proj, Store, ["dead"], deliberate: false); // only a machine pull
		await _recorder.FlushAsync();

		var retired = await Job(enforce: true).DrainAllAsync(CancellationToken.None);

		retired.Should().Be(1);
		(await _memory.GetAsync(Proj, Store, "kept")).Should().NotBeNull(); // deliberate reach spared it
		(await _memory.GetAsync(Proj, Store, "dead")).Should().BeNull();    // machine-only → retired
	}

	[Fact]
	public async Task Enforce_SparesOpenedEntry()
	{
		await Seed(Store, "opened", "dead");
		_recorder.Opened(Proj, Store, "opened"); // a direct engagement counts
		await _recorder.FlushAsync();

		await Job(enforce: true).DrainAllAsync(CancellationToken.None);

		(await _memory.GetAsync(Proj, Store, "opened")).Should().NotBeNull();
		(await _memory.GetAsync(Proj, Store, "dead")).Should().BeNull();
	}

	[Fact]
	public async Task Enforce_SparesYoungEntries()
	{
		await Seed(Store, "fresh");

		// A 30-day min-age: a just-written entry is far younger than the cutoff → not a candidate.
		var retired = await Job(enforce: true, minAge: TimeSpan.FromDays(30)).DrainAllAsync(CancellationToken.None);

		retired.Should().Be(0);
		(await _memory.GetAsync(Proj, Store, "fresh")).Should().NotBeNull();
	}

	[Fact]
	public async Task Enforce_NeverTouchesCuratedStores()
	{
		await Seed("notes", "n1");        // curated — untouchable
		await Seed(Store, "a1");          // quarantine — a candidate

		var retired = await Job(enforce: true).DrainAllAsync(CancellationToken.None);

		retired.Should().Be(1);
		(await _memory.GetAsync(Proj, "notes", "n1")).Should().NotBeNull(); // spared
		(await _memory.GetAsync(Proj, Store, "a1")).Should().BeNull();      // retired
	}

	[Fact]
	public async Task Throttle_SecondPassWithinInterval_IsSkipped()
	{
		await Seed(Store, "a1");
		var job = new MemoryQuarantineGcJob(_factory, _memory, logger: null,
			minAge: AllOld, enforce: true, scanInterval: TimeSpan.FromHours(6));

		var first = await job.DrainAllAsync(CancellationToken.None);
		await Seed(Store, "a2");
		var second = await job.DrainAllAsync(CancellationToken.None);

		first.Should().Be(1);
		second.Should().Be(0); // throttled — the scan interval hasn't elapsed
		(await _memory.GetAsync(Proj, Store, "a2")).Should().NotBeNull();
	}
}
