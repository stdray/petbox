using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Memory;

// The READ side of delivery_events (spec: usage-cost-and-fit-separate). The impression counters
// (surfaced/opened) say an entry keeps APPEARING — they cannot tell "dear and off-target" from
// "cheap and dead-on", which are opposite outcomes. COST (delivered body chars) and FIT (mean
// kRel) can, and these are the surfaces that expose them: the per-store aggregate
// (memory_store_list includeUsage) and the per-entry row (memory_search includeUsage) — before
// this, the table had no reader at all. The old counters stay untouched alongside (back-compat).
public sealed class MemoryUsage2dTests : IDisposable
{
	const string Proj = "proj";
	const string Store = "notes";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryService _memory;
	readonly MemoryUsageRecorder _recorder;

	public MemoryUsage2dTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-usage2d-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _factory), llm: null);
		_recorder = new MemoryUsageRecorder(_factory);
	}

	public void Dispose()
	{
		_recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http()
	{
		var id = new System.Security.Claims.ClaimsIdentity(
			[new System.Security.Claims.Claim("project", Proj), new System.Security.Claims.Claim("scopes", "memory:read,memory:write")], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new System.Security.Claims.ClaimsPrincipal(id) } };
	}

	static PetBox.Core.Features.FeatureFlags Flags()
	{
		var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Memory"] = "true" }).Build();
		return new PetBox.Core.Features.FeatureFlags(cfg);
	}

	async Task Seed(params string[] keys) =>
		await _memory.UpsertAsync(Proj, Store,
			keys.Select(k => new MemoryEntryInput
			{
				Key = k,
				Version = 0,
				Type = "Project",
				Description = $"запись {k} про телеметрию доставки",
				Body = $"тело записи {k} про телеметрию доставки",
			}).ToList(), []);

	// A delivery through the real writer (Ts = now → inside every window).
	void Deliver(string key, int chars, double? kRel, int times = 1)
	{
		for (var i = 0; i < times; i++)
			_recorder.Delivered(Proj, [new MemoryDeliveryEvent(
				Tool: "search", Scope: "project", Store: Store, Key: key,
				DeliveredChars: chars, BodyChars: chars * 2, RowChars: chars + 100,
				Rank: 1, ScoreRaw: 0.02, KRel: kRel, SessionId: null, UsageSource: "deliberate")]);
	}

	// An OLD delivery — the recorder always stamps `now`, so a past event is written straight to
	// the table (the window leg is the only thing this exercises).
	void DeliverAt(string key, int chars, double? kRel, DateTime ts)
	{
		using var db = _factory.NewEnsuredConnection(Proj);
		db.Insert(new DeliveryEvent
		{
			Ts = ts,
			Tool = "search",
			Scope = "project",
			Store = Store,
			Key = key,
			DeliveredChars = chars,
			BodyChars = chars,
			RowChars = chars + 100,
			Rank = 1,
			ScoreRaw = 0.02,
			KRel = kRel,
			UsageSource = "deliberate",
		});
	}

	[Fact]
	public async Task Aggregate_CostAndFit_ComeFromDeliveryEvents()
	{
		await Seed("cheap", "dear", "dead");
		Deliver("cheap", chars: 100, kRel: 1.0, times: 2);   //   200 chars, fit 1.0
		Deliver("dear", chars: 1_000, kRel: 0.2, times: 3);  // 3 000 chars, fit 0.2
		await _recorder.FlushAsync();

		var agg = await _memory.GetUsageAggregateAsync(Proj, Store);

		agg.Cost.Deliveries.Should().Be(5);
		agg.Cost.DeliveredChars.Should().Be(3_200);
		agg.Cost.RowChars.Should().Be(2 * 200 + 3 * 1_100);
		// EVENT-weighted, not entry-weighted: (2×1.0 + 3×0.2) / 5 = 0.52. A mean of the two entry
		// means would read 0.6 and let one lucky entry outvote three expensive misses.
		agg.Cost.AvgKRel.Should().BeApproximately(0.52, 1e-9);
		agg.Cost.EntriesDelivered.Should().Be(2); // "dead" was never delivered
		agg.Cost.WindowDays.Should().Be(30);      // the default window

		// The old counters keep their meaning (nothing surfaced: Delivered writes no impression).
		agg.TotalEntries.Should().Be(3);
		agg.SurfacedAtLeastOnce.Should().Be(0);
	}

	[Fact]
	public async Task Aggregate_Cost_IsWindowed_OldDeliveriesDoNotCount()
	{
		await Seed("k");
		DeliverAt("k", chars: 9_000, kRel: 0.1, DateTime.UtcNow.AddDays(-40)); // outside a 30d window
		Deliver("k", chars: 1_000, kRel: 0.8);                                 // inside it
		await _recorder.FlushAsync();

		var recent = await _memory.GetUsageAggregateAsync(Proj, Store);
		recent.Cost.Deliveries.Should().Be(1);
		recent.Cost.DeliveredChars.Should().Be(1_000);
		recent.Cost.AvgKRel.Should().BeApproximately(0.8, 1e-9);

		// A wider window sees the old bill too — the verdict is a function of the window, and the
		// window is the caller's to choose.
		var wide = await _memory.GetUsageAggregateAsync(Proj, Store, window: TimeSpan.FromDays(90));
		wide.Cost.WindowDays.Should().Be(90);
		wide.Cost.Deliveries.Should().Be(2);
		wide.Cost.DeliveredChars.Should().Be(10_000);
		wide.Cost.AvgKRel.Should().BeApproximately(0.45, 1e-9);
	}

	[Fact]
	public async Task Aggregate_ListingOnlyDeliveries_HaveCostButNoFit()
	{
		await Seed("k");
		Deliver("k", chars: 500, kRel: null, times: 2); // a listing runs no relevance leg
		await _recorder.FlushAsync();

		var agg = await _memory.GetUsageAggregateAsync(Proj, Store);

		agg.Cost.DeliveredChars.Should().Be(1_000); // it still cost context
		agg.Cost.AvgKRel.Should().BeNull();         // but nothing measured its fit — not a 0
	}

	[Fact]
	public async Task StoreList_IncludeUsage_CarriesCostAndFit()
	{
		await Seed("k");
		Deliver("k", chars: 400, kRel: 0.25, times: 2);
		await _recorder.FlushAsync();

		var plain = await MemoryTools.StoreListAsync(Http(), Flags(), _db, _memory, Proj);
		plain.Stores.Single(s => s.Name == Store).Usage.Should().BeNull(); // still off by default

		var res = await MemoryTools.StoreListAsync(Http(), Flags(), _db, _memory, Proj, includeUsage: true);
		var usage = res.Stores.Single(s => s.Name == Store).Usage;

		usage.Should().NotBeNull();
		usage!.DeliveredChars.Should().Be(800);
		usage.Deliveries.Should().Be(2);
		usage.AvgKRel.Should().BeApproximately(0.25, 1e-9);
		usage.EntriesDelivered.Should().Be(1);
		usage.WindowDays.Should().Be(30);
		usage.TotalEntries.Should().Be(1); // the pre-existing aggregate is intact
	}

	[Fact]
	public async Task StoreList_UsageWindowDays_ScopesTheCost()
	{
		await Seed("k");
		DeliverAt("k", chars: 5_000, kRel: 0.1, DateTime.UtcNow.AddDays(-40));
		await _recorder.FlushAsync();

		var narrow = await MemoryTools.StoreListAsync(Http(), Flags(), _db, _memory, Proj, includeUsage: true);
		narrow.Stores.Single(s => s.Name == Store).Usage!.DeliveredChars.Should().Be(0);

		var wide = await MemoryTools.StoreListAsync(Http(), Flags(), _db, _memory, Proj, includeUsage: true, usageWindowDays: 90);
		var usage = wide.Stores.Single(s => s.Name == Store).Usage!;
		usage.WindowDays.Should().Be(90);
		usage.DeliveredChars.Should().Be(5_000);
	}

	[Fact]
	public async Task Search_IncludeUsage_CarriesPerEntryCostAndFit()
	{
		await Seed("k");
		// Two earlier searches: each records an impression AND a delivery (that is what the search
		// path does). All-time on the per-entry surface — this is a read, not a verdict, so it
		// reports the entry's whole life (the GC is the one that judges on a window).
		_recorder.Surfaced(Proj, Store, ["k"]);
		Deliver("k", chars: 300, kRel: 1.0);
		_recorder.Surfaced(Proj, Store, ["k"]);
		Deliver("k", chars: 300, kRel: 0.5);
		await _recorder.FlushAsync();

		// Usage is read BEFORE this call records its own delivery, so the numbers are the two
		// events above — the search does not count itself.
		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", store: Store, includeUsage: true);
		var hit = res.Items.Single();

		hit.DeliveredChars.Should().Be(600);
		hit.AvgKRel.Should().BeApproximately(0.75, 1e-9);
		hit.Surfaced.Should().NotBeNull(); // the old counters still ride along

		var plain = await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", store: Store);
		plain.Items[0].DeliveredChars.Should().BeNull(); // omitted unless asked
		plain.Items[0].AvgKRel.Should().BeNull();
	}

	[Fact]
	public async Task GetUsage_DecoratesTheCounterRow_ButAListingStillCountsNothing()
	{
		await Seed("counted", "listed");
		_recorder.Surfaced(Proj, Store, ["counted"]); // an impression → a counter row exists
		Deliver("counted", chars: 250, kRel: 0.4);
		Deliver("listed", chars: 900, kRel: null);    // a listing: cost, but no impression
		await _recorder.FlushAsync();

		var usage = await _memory.GetUsageAsync(Proj, Store);

		var u = usage["counted"];
		u.Surfaced.Should().Be(1);
		u.DeliveredChars.Should().Be(250); // the counter row now carries cost…
		u.Deliveries.Should().Be(1);
		u.AvgKRel.Should().BeApproximately(0.4, 1e-9); // …and fit
													   // A listing counts NO impression (curation, by contract), so it must not sprout a usage
													   // row here — its cost is not lost, the store aggregate reads delivery_events directly.
		usage.Should().NotContainKey("listed");
		(await _memory.GetUsageAggregateAsync(Proj, Store)).Cost.DeliveredChars.Should().Be(1_150);
	}
}
