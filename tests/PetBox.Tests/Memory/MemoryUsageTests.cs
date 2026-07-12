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
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Memory;

// The usage-telemetry promises (spec: memory-usage-observability + usage-counts-what-was-sent):
// a memory_search answer counts an impression for the entries it actually SENT (post-limit,
// post-budget — a row cut before the wire is not surfaced), a listing delivers too (surfaced,
// but never deliberate), a direct get counts an engagement, internal IMemoryService traffic
// counts nothing, counters surface only under includeUsage, and the read path never waits on
// the write (the recorder is a queue — tests flush explicitly).
public sealed class MemoryUsageTests : IDisposable
{
	const string Proj = "proj";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;
	readonly MemoryUsageRecorder _recorder;

	public MemoryUsageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memusage-" + Guid.NewGuid().ToString("N"));
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

	async Task Seed(params string[] keys)
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes",
			keys.Select(k => new MemoryEntryInput
			{
				Key = k,
				Version = 0,
				Type = "Project",
				Description = $"запись {k} про телеметрию",
				Body = $"тело {k}",
			}).ToList(), []);
	}

	// N entries whose bodies are big enough to make the response budget bite.
	async Task SeedBig(int count, int bodyChars)
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes",
			Enumerable.Range(1, count).Select(i => new MemoryEntryInput
			{
				Key = $"big{i}",
				Version = 0,
				Type = "Project",
				Description = $"запись big{i} про телеметрию",
				Body = new string('я', bodyChars),
			}).ToList(), []);
	}

	[Fact]
	public async Task SearchAnswer_CountsImpression_ForReturnedEntries()
	{
		await Seed("u1", "u2");

		await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes");
		await _recorder.FlushAsync();

		var usage = await _memory.GetUsageAsync(Proj, "notes");
		usage["u1"].Surfaced.Should().Be(1);
		usage["u1"].Opened.Should().Be(0);
		usage["u1"].LastHitAt.Should().NotBeNull();
		usage["u2"].Surfaced.Should().Be(1);
	}

	[Fact]
	public async Task Get_CountsEngagement_NotImpression()
	{
		await Seed("u1");

		await MemoryTools.GetAsync(Http(), Flags(), _db, _memory, _recorder, Proj, "notes", "u1");
		await _recorder.FlushAsync();

		var usage = await _memory.GetUsageAsync(Proj, "notes");
		usage["u1"].Opened.Should().Be(1);
		usage["u1"].Surfaced.Should().Be(0);
	}

	// spec addressed-read-batched: the counter measures ENTRIES handed over, not calls — a batch
	// get of N keys is N engagements (one Opened each), and a key that matched nothing (never
	// handed over) is not counted at all.
	[Fact]
	public async Task BatchGet_CountsEngagement_PerReturnedKey()
	{
		await Seed("u1", "u2");

		var got = await MemoryTools.GetAsync(Http(), Flags(), _db, _memory, _recorder, Proj, "notes",
			keys: ["u1", "u2", "no-such-key"]);
		await _recorder.FlushAsync();

		got.Entries.Select(e => e.Key).Should().Equal("u1", "u2");
		var usage = await _memory.GetUsageAsync(Proj, "notes");
		usage["u1"].Opened.Should().Be(1);
		usage["u2"].Opened.Should().Be(1);
		usage["u1"].Surfaced.Should().Be(0);
		usage.Should().NotContainKey("no-such-key");
	}

	// A listing IS a delivery — its rows land in the caller's context exactly like a search's, so
	// they are impressions. But no relevance leg ran behind them, so they never count as
	// deliberate: DeliberateCount stays the honest "this fact proved its worth" signal GC reads.
	[Fact]
	public async Task Listing_CountsSurfaced_ButNotDeliberate()
	{
		await Seed("u1");

		await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, scope: "project", store: "notes");
		await _recorder.FlushAsync();

		var u = (await _memory.GetUsageAsync(Proj, "notes"))["u1"];
		u.Surfaced.Should().Be(1);
		u.Deliberate.Should().Be(0);
		u.Opened.Should().Be(0);

		// …and the deliberate cut the GC looks at stays empty for a listing-only entry.
		var agg = await _memory.GetUsageAggregateAsync(Proj, "notes");
		agg.SurfacedAtLeastOnce.Should().Be(1);
		agg.DeliberatelySurfacedAtLeastOnce.Should().Be(0);
	}

	// Internal machine path (the distiller's neighbor probe) reaches IMemoryService directly and
	// counts nothing — only the agent-facing adapters record usage.
	[Fact]
	public async Task DirectServiceTraffic_CountsNothing()
	{
		await Seed("u1");

		await _memory.SearchAsync(Proj, "notes", "телеметрию", type: null);
		await _memory.GetAsync(Proj, "notes", "u1");
		await _recorder.FlushAsync();

		(await _memory.GetUsageAsync(Proj, "notes")).Should().BeEmpty();
	}

	// Impressions are counted on what was SENT, not on what was found: rows the response budget
	// prefix-cut never reached the agent's context, so they must not look surfaced
	// (spec: usage-counts-what-was-sent).
	[Fact]
	public async Task BudgetCutRows_AreNotCountedSurfaced()
	{
		await SeedBig(12, 5_000);

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию",
			scope: "project", store: "notes", limit: 20, bodyLen: -1);
		await _recorder.FlushAsync();

		res.Truncated.Should().BeTrue("12 × 5k-char bodies overflow the ~30k output budget");
		res.Items.Count.Should().BeLessThan(12);

		var usage = await _memory.GetUsageAsync(Proj, "notes");
		usage.Keys.Should().BeEquivalentTo(res.Items.Select(i => i.Key), "only the delivered rows are impressions");
		usage.Values.Should().OnlyContain(u => u.Surfaced == 1);
	}

	[Fact]
	public async Task IncludeUsage_SurfacesCounters_DefaultOmitsThem()
	{
		await Seed("u1");
		await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes");
		await _recorder.FlushAsync();

		// memory_search returns the typed record directly (errors throw → McpErrorEnvelopeFilter
		// renders them on the wire; unit tests read the concrete success value).
		var plain = await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes");
		plain.Items[0].Surfaced.Should().BeNull(); // default off — context economy

		var with = await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes", includeUsage: true);
		with.Items[0].Surfaced.Should().BeGreaterThanOrEqualTo(1);
		with.Items[0].Opened.Should().Be(0);
	}

	[Fact]
	public async Task Search_CountsImpressions_PerContainerStore()
	{
		await Seed("u1");

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию");
		res.Items.Should().NotBeEmpty();
		await _recorder.FlushAsync();

		(await _memory.GetUsageAsync(Proj, "notes"))["u1"].Surfaced.Should().Be(1);
	}

	[Fact]
	public async Task Aggregate_OverMixOfUsedAndDeadEntries()
	{
		await Seed("u1", "u2", "u3", "u4", "u5");
		// u1,u2 surfaced (impressions); u2 also opened; u3,u4,u5 never touched → the dead tail.
		_recorder.Surfaced(Proj, "notes", ["u1", "u2"]);
		_recorder.Opened(Proj, "notes", "u2");
		await _recorder.FlushAsync();

		var agg = await _memory.GetUsageAggregateAsync(Proj, "notes");

		agg.TotalEntries.Should().Be(5);
		agg.SurfacedAtLeastOnce.Should().Be(2);
		agg.OpenedAtLeastOnce.Should().Be(1);
		agg.SurfacedFraction.Should().BeApproximately(0.4, 1e-9);
		agg.OpenedFraction.Should().BeApproximately(0.2, 1e-9);
		agg.MedianLastHitAt.Should().NotBeNull();
		agg.DeadTail.Count.Should().Be(3);
		agg.DeadTail.TopKeys.Should().BeEquivalentTo(new[] { "u3", "u4", "u5" });
	}

	[Fact]
	public async Task Aggregate_DeadTail_CapsAtLimit_OldestFirst()
	{
		await Seed("d1", "d2", "d3", "d4"); // none surfaced → all dead

		var agg = await _memory.GetUsageAggregateAsync(Proj, "notes", deadTailLimit: 2);

		agg.DeadTail.Count.Should().Be(4);          // the full count is not capped
		agg.DeadTail.TopKeys.Should().HaveCount(2);  // the sample is
	}

	[Fact]
	public async Task Aggregate_EmptyStore_IsAllZeroNoMedian()
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);

		var agg = await _memory.GetUsageAggregateAsync(Proj, "notes");

		agg.TotalEntries.Should().Be(0);
		agg.SurfacedAtLeastOnce.Should().Be(0);
		agg.OpenedAtLeastOnce.Should().Be(0);
		agg.SurfacedFraction.Should().Be(0);
		agg.OpenedFraction.Should().Be(0);
		agg.MedianLastHitAt.Should().BeNull();
		agg.DeadTail.Count.Should().Be(0);
		agg.DeadTail.TopKeys.Should().BeEmpty();
	}

	[Fact]
	public async Task StoreList_IncludeUsage_AttachesAggregate_DefaultOmitsIt()
	{
		await Seed("u1", "u2");
		_recorder.Surfaced(Proj, "notes", ["u1"]);
		await _recorder.FlushAsync();

		var plain = await MemoryTools.StoreListAsync(Http(), Flags(), _db, _memory, Proj);
		plain.Stores.Should().ContainSingle();
		plain.Stores[0].Usage.Should().BeNull(); // default off

		var with = await MemoryTools.StoreListAsync(Http(), Flags(), _db, _memory, Proj, includeUsage: true);
		var row = with.Stores.Single(s => s.Name == "notes");
		row.Usage.Should().NotBeNull();
		row.Usage!.TotalEntries.Should().Be(2);
		row.Usage.SurfacedAtLeastOnce.Should().Be(1);
		row.Usage.DeadCount.Should().Be(1);
		row.Usage.DeadTailKeys.Should().Contain("u2");
	}

	[Fact]
	public async Task Recorder_AccumulatesAcrossEvents()
	{
		await Seed("u1");
		_recorder.Surfaced(Proj, "notes", ["u1"]);
		_recorder.Surfaced(Proj, "notes", ["u1"]);
		_recorder.Opened(Proj, "notes", "u1");
		await _recorder.FlushAsync();

		var u = (await _memory.GetUsageAsync(Proj, "notes"))["u1"];
		u.Surfaced.Should().Be(2);
		u.Opened.Should().Be(1);
	}

	// Honest usage signal (spec: memoverhaul): a deliberate search (the default) counts toward
	// BOTH the raw surfaced total and the deliberate cut; a machine pull (usage:"machine",
	// e.g. an automatic hook prime) bumps only the raw total — never the deliberate cut GC trusts.
	[Fact]
	public async Task DeliberateSearch_CountsSurfacedAndDeliberate()
	{
		await Seed("u1");

		await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes");
		await _recorder.FlushAsync();

		var u = (await _memory.GetUsageAsync(Proj, "notes"))["u1"];
		u.Surfaced.Should().Be(1);
		u.Deliberate.Should().Be(1);
	}

	[Fact]
	public async Task MachineSearch_CountsSurfaced_ButNotDeliberate()
	{
		await Seed("u1");

		await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes", usageSource: "machine");
		await _recorder.FlushAsync();

		var u = (await _memory.GetUsageAsync(Proj, "notes"))["u1"];
		u.Surfaced.Should().Be(1); // still an impression
		u.Deliberate.Should().Be(0); // but not a proven-value one
	}

	[Fact]
	public async Task Aggregate_DeliberateCut_CountsOnlyDeliberatelySurfaced()
	{
		await Seed("u1", "u2", "u3");
		_recorder.Surfaced(Proj, "notes", ["u1", "u2"], deliberate: false); // machine pulls
		_recorder.Surfaced(Proj, "notes", ["u1"], deliberate: true);        // u1 also deliberately reached
		await _recorder.FlushAsync();

		var agg = await _memory.GetUsageAggregateAsync(Proj, "notes");

		agg.SurfacedAtLeastOnce.Should().Be(2);              // u1, u2 surfaced (any source)
		agg.DeliberatelySurfacedAtLeastOnce.Should().Be(1);  // only u1 proved value
	}
}
