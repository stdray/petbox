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

// The delivery-event promises (spec: usage-cost-and-fit-separate + memory-usage-observability):
// every entry a tool call actually SENT lands in delivery_events with its COST (deliveredChars /
// bodyChars / rowChars) and its FIT (rank / scoreRaw / kRel) kept as SEPARATE raw components.
// kRel is normalized WITHIN the request (score over the request's top-1) because raw RRF has no
// absolute scale; memory_get is a perfect fit by construction (kRel 1, whole body delivered); a
// listing ran no relevance leg (no score at all). entry_usage keeps working alongside, untouched.
public sealed class MemoryDeliveryEventsTests : IDisposable
{
	const string Proj = "proj";

	// Longer than the default ~240-char search snippet, so a search delivers only PART of the
	// body — which is exactly what deliveredChars vs bodyChars exists to make visible.
	static readonly string LongBody = string.Concat(Enumerable.Repeat("тело про телеметрию доставки. ", 30));

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryService _memory;
	readonly MemoryUsageRecorder _recorder;

	public MemoryDeliveryEventsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memdeliv-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_memory = new MemoryService(new MemoryStore(_db, _factory), llm: null);
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
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(id) } };
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
				Description = $"запись {k} про телеметрию доставки",
				Body = LongBody,
			}).ToList(), []);
	}

	List<DeliveryEvent> Events()
	{
		using var db = _factory.NewEnsuredConnection(Proj);
		return db.Deliveries.OrderBy(e => e.Id).ToList();
	}

	[Fact]
	public async Task Search_RecordsOneEvent_PerDeliveredEntry_WithCostAndFit()
	{
		await Seed("u1", "u2");

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes");
		await _recorder.FlushAsync();

		res.Items.Should().HaveCount(2);
		var events = Events();
		events.Should().HaveCount(2);
		events.Select(e => e.Key).Should().BeEquivalentTo(res.Items.Select(i => i.Key));

		foreach (var (e, i) in events.Zip(Enumerable.Range(0, events.Count)))
		{
			var row = res.Items[i];
			e.Tool.Should().Be("search");
			e.Scope.Should().Be("project");
			e.Store.Should().Be("notes");
			e.Key.Should().Be(row.Key);           // events follow the DELIVERED order…
			e.Rank.Should().Be(i + 1);            // …and rank is that 1-based position
			e.UsageSource.Should().Be("deliberate");
			e.Ts.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

			// COST: what was sent (the snippet) vs the whole entry vs the row's wire price.
			e.DeliveredChars.Should().Be(row.Body!.Length);
			e.BodyChars.Should().Be(LongBody.Length);
			e.DeliveredChars.Should().BeLessThan(e.BodyChars); // the default snippet cut the body
			e.RowChars.Should().BeGreaterThan(e.DeliveredChars); // description, tags, envelope

			// FIT: the raw fused score is kept AND its within-request normalization.
			e.ScoreRaw.Should().NotBeNull();
			e.ScoreRaw!.Value.Should().BeGreaterThan(0);
			e.KRel.Should().NotBeNull();
			e.KRel!.Value.Should().BeInRange(0, 1);
		}

		// kRel is score / the request's top-1 score → the best hit of the request is exactly 1.
		events.Max(e => e.KRel)!.Value.Should().BeApproximately(1, 1e-9);
		var top = events.Max(e => e.ScoreRaw)!.Value;
		foreach (var e in events)
			e.KRel!.Value.Should().BeApproximately(e.ScoreRaw!.Value / top, 1e-9);
	}

	// A get delivers the WHOLE body of an entry the caller named: cost is the full body and fit
	// is perfect by construction — no fused score exists behind it to normalize.
	[Fact]
	public async Task Get_RecordsEvent_FullBodyDelivered_AndKRelOne()
	{
		await Seed("u1");

		await MemoryTools.GetAsync(Http(), Flags(), _db, _memory, _recorder, Proj, "notes", "u1");
		await _recorder.FlushAsync();

		var e = Events().Should().ContainSingle().Subject;
		e.Tool.Should().Be("get");
		e.Scope.Should().Be("project");
		e.Store.Should().Be("notes");
		e.Key.Should().Be("u1");
		e.Rank.Should().Be(1);
		e.DeliveredChars.Should().Be(LongBody.Length);
		e.BodyChars.Should().Be(LongBody.Length);
		e.DeliveredChars.Should().Be(e.BodyChars); // nothing was cut
		e.RowChars.Should().BeGreaterThan(e.DeliveredChars);
		e.KRel.Should().Be(1);
		e.ScoreRaw.Should().BeNull(); // no relevance leg ran
		e.UsageSource.Should().Be("deliberate");
	}

	// A listing is curation, not retrieval: it delivers rows (a cost) with no relevance behind
	// them (no fit). Recorded as such — never with a fabricated score.
	[Fact]
	public async Task Listing_RecordsEvents_WithCost_ButNoScore()
	{
		await Seed("u1", "u2");

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, scope: "project", store: "notes");
		await _recorder.FlushAsync();

		var events = Events();
		events.Should().HaveCount(res.Items.Count);
		events.Should().OnlyContain(e => e.Tool == "listing" && e.ScoreRaw == null && e.KRel == null);
		events.Should().OnlyContain(e => e.BodyChars == LongBody.Length && e.DeliveredChars > 0);
	}

	// The honest usage split rides on the event too (same cut entry_usage.DeliberateCount records).
	[Fact]
	public async Task MachineSearch_RecordsEvents_TaggedMachine()
	{
		await Seed("u1");

		await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes", usageSource: "machine");
		await _recorder.FlushAsync();

		Events().Should().OnlyContain(e => e.UsageSource == "machine");
	}

	// The delivery events are an ADDITION, not a replacement: entry_usage keeps counting exactly
	// as before (the fast counter cache stays the read surface for "how often").
	[Fact]
	public async Task DeliveryEvents_DoNotReplace_EntryUsageCounters()
	{
		await Seed("u1");

		await MemoryTools.SearchAsync(Http(), Flags(), _db, _memory, _recorder, "телеметрию", scope: "project", store: "notes");
		await MemoryTools.GetAsync(Http(), Flags(), _db, _memory, _recorder, Proj, "notes", "u1");
		await _recorder.FlushAsync();

		var u = (await _memory.GetUsageAsync(Proj, "notes"))["u1"];
		u.Surfaced.Should().Be(1);
		u.Opened.Should().Be(1);
		Events().Should().HaveCount(2); // one for the search row, one for the get
	}
}
