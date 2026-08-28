using LinqToDB;
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
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_store = new MemoryStore(_db.Factory(), _factory);
		_memory = new MemoryService(_store, llm: null);
		_recorder = new MemoryUsageRecorder(_factory);
	}

	public void Dispose()
	{
		_recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	MemoryQuarantineGcJob Job(bool enforce, TimeSpan? minAge = null) =>
		new(new ProjectCatalog(_db.Factory()), _memory, logger: null, minAge: minAge ?? AllOld, enforce: enforce, scanInterval: NoThrottle);

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

		// A 90-day min-age: a just-written entry is far younger than the cutoff → not a candidate.
		var retired = await Job(enforce: true, minAge: TimeSpan.FromDays(90)).DrainAllAsync(CancellationToken.None);

		retired.Should().Be(0);
		(await _memory.GetAsync(Proj, Store, "fresh")).Should().NotBeNull();
	}

	// Backdates an active entry's Created column directly — the public UpsertAsync path always
	// stamps `now`, so this is the only way to exercise a real elapsed-age boundary rather than
	// the AllOld trick (cutoff pushed into the future) the rest of this file uses.
	async Task Backdate(string key, TimeSpan age)
	{
		using var db = _factory.NewEnsuredConnection(Proj);
		await db.Entries.Where(e => e.Store == Store && e.Key == key && e.ActiveTo == null)
			.Set(e => e.Created, DateTime.UtcNow - age)
			.UpdateAsync();
	}

	[Fact]
	public async Task Enforce_DefaultMinAge_Is90Days_60dSpared_100dRetired()
	{
		// Card chore/gc-min-age-90: MinAgeDays raised 30 -> 90 (owner decision — episodic-demand
		// facts legitimately go unreached for weeks; 24% of candidates are found within 48h once
		// they surface, and a manual read of 10 live candidates found 8-9/10 were correct reusable
		// facts, not noise). This exercises the class DEFAULT (no minAge passed to the
		// constructor) so it fails loudly if the default constant ever regresses.
		await Seed(Store, "young60", "old100");
		await Backdate("young60", TimeSpan.FromDays(60));
		await Backdate("old100", TimeSpan.FromDays(100));

		var job = new MemoryQuarantineGcJob(new ProjectCatalog(_db.Factory()), _memory, logger: null,
			enforce: true, scanInterval: NoThrottle); // minAge omitted -> DefaultMinAge

		var retired = await job.DrainAllAsync(CancellationToken.None);

		retired.Should().Be(1);
		(await _memory.GetAsync(Proj, Store, "young60")).Should().NotBeNull(); // 60d < 90d -> spared
		(await _memory.GetAsync(Proj, Store, "old100")).Should().BeNull();     // 100d >= 90d -> retired
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

	// --- the two-dimensional rule (spec: usage-cost-and-fit-separate) --------------------
	// COST (delivered body chars) and FIT (mean kRel) are what the impression counters cannot
	// say. `Delivered` writes delivery_events only — it never bumps entry_usage — so every
	// entry below has deliberate=0 / opened=0: under the OLD rule all of them died.

	// N deliveries of one entry: `chars` of body at fit `kRel` each.
	void Deliver(string key, int chars, double? kRel, int times = 1)
	{
		for (var i = 0; i < times; i++)
			_recorder.Delivered(Proj, [new MemoryDeliveryEvent(
				Tool: "search", Scope: "project", Store: Store, Key: key,
				DeliveredChars: chars, BodyChars: chars, RowChars: chars + 100,
				Rank: 1, ScoreRaw: 0.02, KRel: kRel, TransportSessionId: null, UsageSource: "deliberate")]);
	}

	[Fact]
	public async Task Enforce_RetiresExpensiveOffTargetEntry_ButSparesCheapPreciseOne()
	{
		await Seed(Store, "boar", "precise");
		// The noise boar: 12k chars of context spent over six deliveries, mean fit 0.2 — it keeps
		// getting paid for and keeps not being the answer.
		Deliver("boar", chars: 2_000, kRel: 0.2, times: 6);
		// The precise index row: the snippet that ANSWERS the question, so it is the top hit every
		// time and nobody ever needs to open it. 1.2k chars total, fit 1.0. The old
		// (deliberate == 0 && opened == 0) rule killed exactly this entry — the better it is, the
		// more certainly it died.
		Deliver("precise", chars: 200, kRel: 1.0, times: 6);
		await _recorder.FlushAsync();

		var retired = await Job(enforce: true).DrainAllAsync(CancellationToken.None);

		retired.Should().Be(1);
		(await _memory.GetAsync(Proj, Store, "boar")).Should().BeNull();       // dear and off-target
		(await _memory.GetAsync(Proj, Store, "precise")).Should().NotBeNull(); // cheap and dead-on
	}

	[Fact]
	public async Task Enforce_SparesExpensiveEntryThatActuallyFits()
	{
		await Seed(Store, "big");
		Deliver("big", chars: 4_000, kRel: 0.9, times: 5); // 20k chars — but it IS the answer

		await _recorder.FlushAsync();

		var retired = await Job(enforce: true).DrainAllAsync(CancellationToken.None);

		retired.Should().Be(0); // cost alone is not a verdict; fit is the other half
		(await _memory.GetAsync(Proj, Store, "big")).Should().NotBeNull();
	}

	[Fact]
	public async Task Enforce_ThresholdsAreConfigurable()
	{
		await Seed(Store, "boar");
		Deliver("boar", chars: 2_000, kRel: 0.2, times: 6); // 12k chars at fit 0.2 — a boar by default
		await _recorder.FlushAsync();

		// A stricter fit floor (0.1) says 0.2 is good enough → the same entry is no longer a boar.
		var job = new MemoryQuarantineGcJob(new ProjectCatalog(_db.Factory()), _memory, logger: null,
			minAge: AllOld, enforce: true, scanInterval: NoThrottle, maxAvgKRel: 0.1);

		(await job.DrainAllAsync(CancellationToken.None)).Should().Be(0);
		(await _memory.GetAsync(Proj, Store, "boar")).Should().NotBeNull();
	}

	// --- cost/fit axis: combined (default) vs deliberate-only (card usage-delivery-mixes-
	// machine-traffic, spec memory-usage-observability) ------------------------------------
	// A pure-machine delivery: `usage.Delivered` tagged UsageSource "machine" — never through
	// `usage.Surfaced`/`Opened`, so entry_usage stays empty for it (no counter row at all),
	// exactly like a real automated wiring-kit pull.
	void DeliverMachine(string key, int chars, double? kRel, int times = 1)
	{
		for (var i = 0; i < times; i++)
			_recorder.Delivered(Proj, [new MemoryDeliveryEvent(
				Tool: "search", Scope: "project", Store: Store, Key: key,
				DeliveredChars: chars, BodyChars: chars, RowChars: chars + 100,
				Rank: 1, ScoreRaw: 0.02, KRel: kRel, TransportSessionId: null, UsageSource: "machine")]);
	}

	[Fact]
	public async Task Enforce_DefaultAxis_CountsMachineCost_AutomatedHighFitEntryIsSpared()
	{
		await Seed(Store, "auto");
		// Automated (machine) traffic only, but a GOOD fit — 5k chars (under the 10k boar floor)
		// at 0.9 fit. Under today's unchanged default (combined axis) this reads as "reached and
		// on-target", same as if a human had searched for it.
		DeliverMachine("auto", chars: 5_000, kRel: 0.9);
		await _recorder.FlushAsync();

		var retired = await Job(enforce: true).DrainAllAsync(CancellationToken.None); // excludeMachineFromCost defaults false

		retired.Should().Be(0);
		(await _memory.GetAsync(Proj, Store, "auto")).Should().NotBeNull();
	}

	[Fact]
	public async Task Enforce_ExcludeMachineFromCost_SameAutomatedEntry_LooksNeverReached_RiskDemonstrated()
	{
		await Seed(Store, "auto");
		// The exact same entry/delivery as the test above.
		DeliverMachine("auto", chars: 5_000, kRel: 0.9);
		await _recorder.FlushAsync();

		var job = new MemoryQuarantineGcJob(new ProjectCatalog(_db.Factory()), _memory, logger: null,
			minAge: AllOld, enforce: true, scanInterval: NoThrottle, excludeMachineFromCost: true);
		var retired = await job.DrainAllAsync(CancellationToken.None);

		// This is the card's stated Risk, reproduced: narrowing cost/fit to deliberate-only makes
		// this entry's real (high-fit) cost invisible — its deliberate-cut deliveredChars is 0 and
		// it was never Surfaced/Opened either, so it now reads as "never reached" and IS retired,
		// even though it is a precise, actively-used (if automated) entry. This is exactly why the
		// config defaults to false: an operator must review a before/after report on live data
		// before opting a store/GC into this axis.
		retired.Should().Be(1);
		(await _memory.GetAsync(Proj, Store, "auto")).Should().BeNull();
	}

	[Fact]
	public async Task Enforce_ExcludeMachineFromCost_DeliberateNoiseBoar_IsStillRetired()
	{
		await Seed(Store, "boar");
		// The SAME noise-boar shape as Enforce_RetiresExpensiveOffTargetEntry_ButSparesCheapPreciseOne
		// (12k chars at fit 0.2), but tagged deliberate this time — the deliberate-only axis must
		// still catch a genuinely deliberate noise boar, not just skip judging it.
		for (var i = 0; i < 6; i++)
			_recorder.Delivered(Proj, [new MemoryDeliveryEvent(
				Tool: "search", Scope: "project", Store: Store, Key: "boar",
				DeliveredChars: 2_000, BodyChars: 2_000, RowChars: 2_100,
				Rank: 1, ScoreRaw: 0.02, KRel: 0.2, TransportSessionId: null, UsageSource: "deliberate")]);
		await _recorder.FlushAsync();

		var job = new MemoryQuarantineGcJob(new ProjectCatalog(_db.Factory()), _memory, logger: null,
			minAge: AllOld, enforce: true, scanInterval: NoThrottle, excludeMachineFromCost: true);
		var retired = await job.DrainAllAsync(CancellationToken.None);

		retired.Should().Be(1);
		(await _memory.GetAsync(Proj, Store, "boar")).Should().BeNull();
	}

	[Fact]
	public async Task Throttle_SecondPassWithinInterval_IsSkipped()
	{
		await Seed(Store, "a1");
		var job = new MemoryQuarantineGcJob(new ProjectCatalog(_db.Factory()), _memory, logger: null,
			minAge: AllOld, enforce: true, scanInterval: TimeSpan.FromHours(6));

		var first = await job.DrainAllAsync(CancellationToken.None);
		await Seed(Store, "a2");
		var second = await job.DrainAllAsync(CancellationToken.None);

		first.Should().Be(1);
		second.Should().Be(0); // throttled — the scan interval hasn't elapsed
		(await _memory.GetAsync(Proj, Store, "a2")).Should().NotBeNull();
	}
}
