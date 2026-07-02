using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Memory;

// The usage-telemetry promises (spec: memory-usage-observability): a memory.search (with q)
// answer counts an impression for the RETURNED entries, a direct get counts an
// engagement, listing counts nothing, internal IMemoryService traffic counts nothing,
// counters surface only under includeUsage, and the read path never waits on the write
// (the recorder is a queue — tests flush explicitly).
[Collection("DataModule")]
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
		MigrationRunner.Run(cs);
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
				Description = $"запись {k} про телеметрию",
				Body = $"тело {k}",
			}).ToList(), []);
	}

	[Fact]
	public async Task SearchAnswer_CountsImpression_ForReturnedEntries()
	{
		await Seed("u1", "u2");

		await MemoryTools.SearchAsync(Http(), Flags(), _memory, _recorder, "телеметрию", scope: "project", store: "notes");
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

		await MemoryTools.GetAsync(Http(), Flags(), _memory, _recorder, Proj, "notes", "u1");
		await _recorder.FlushAsync();

		var usage = await _memory.GetUsageAsync(Proj, "notes");
		usage["u1"].Opened.Should().Be(1);
		usage["u1"].Surfaced.Should().Be(0);
	}

	[Fact]
	public async Task Listing_AndDirectServiceTraffic_CountNothing()
	{
		await Seed("u1");

		// Bulk listing (memory.search without q = curation) + internal machine path (the
		// distiller's neighbor probe).
		await MemoryTools.SearchAsync(Http(), Flags(), _memory, _recorder, scope: "project", store: "notes");
		await _memory.SearchAsync(Proj, "notes", "телеметрию", type: null);
		await _memory.GetAsync(Proj, "notes", "u1");
		await _recorder.FlushAsync();

		(await _memory.GetUsageAsync(Proj, "notes")).Should().BeEmpty();
	}

	[Fact]
	public async Task IncludeUsage_SurfacesCounters_DefaultOmitsThem()
	{
		await Seed("u1");
		await MemoryTools.SearchAsync(Http(), Flags(), _memory, _recorder, "телеметрию", scope: "project", store: "notes");
		await _recorder.FlushAsync();

		// memory.search returns the typed record directly (errors throw → McpErrorEnvelopeFilter
		// renders them on the wire; unit tests read the concrete success value).
		var plain = await MemoryTools.SearchAsync(Http(), Flags(), _memory, _recorder, "телеметрию", scope: "project", store: "notes");
		plain.Items[0].Surfaced.Should().BeNull(); // default off — context economy

		var with = await MemoryTools.SearchAsync(Http(), Flags(), _memory, _recorder, "телеметрию", scope: "project", store: "notes", includeUsage: true);
		with.Items[0].Surfaced.Should().BeGreaterThanOrEqualTo(1);
		with.Items[0].Opened.Should().Be(0);
	}

	[Fact]
	public async Task Search_CountsImpressions_PerContainerStore()
	{
		await Seed("u1");

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _memory, _recorder, "телеметрию");
		res.Items.Should().NotBeEmpty();
		await _recorder.FlushAsync();

		(await _memory.GetUsageAsync(Proj, "notes"))["u1"].Surfaced.Should().Be(1);
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
}
