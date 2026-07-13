using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;
using Xunit.Abstractions;

namespace PetBox.Tests.Memory;

// The row-weight promise (spec: row-weight-bounded): a search/listing ROW is bounded on BOTH
// halves. The body already followed the uniform bodyLen contract; the HEAD did not — the
// description rode full-length on every row, so a bodyLen:0 answer still cost hundreds of chars
// per row without a single char of body. The description is now capped by the SAME truncation
// contract (ModuleMcp.Body); the full text stays one memory_get away.
public sealed class MemoryRowWeightTests : IDisposable
{
	const string Proj = "proj";
	const int Cap = 160; // MemoryTools.DescriptionSnippet

	// A description written as if it were a body — the exact abuse the cap bounds.
	static readonly string LongDescription = string.Concat(Enumerable.Repeat("описание записи, слишком длинное для шапки строки. ", 10));

	readonly ITestOutputHelper _output;
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryService _memory;
	readonly MemoryUsageRecorder _recorder;

	public MemoryRowWeightTests(ITestOutputHelper output)
	{
		_output = output;
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memrow-" + Guid.NewGuid().ToString("N"));
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

	async Task Seed(int count)
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes",
			Enumerable.Range(1, count).Select(i => new MemoryEntryInput
			{
				Key = $"row{i}",
				Version = 0,
				Type = "Project",
				Description = LongDescription,
				Body = string.Concat(Enumerable.Repeat("тело записи про телеметрию. ", 20)),
				Tags = ["memory", "telemetry"],
			}).ToList(), []);
	}

	// The same shape in ASCII — for the wire-cost measurement (see below).
	static readonly string AsciiDescription = string.Concat(Enumerable.Repeat("a description that was written like a body, far too long for a row head. ", 5));

	async Task SeedAscii(int count)
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		await _memory.UpsertAsync(Proj, "notes",
			Enumerable.Range(1, count).Select(i => new MemoryEntryInput
			{
				Key = $"row{i}",
				Version = 0,
				Type = "Project",
				Description = AsciiDescription,
				Body = string.Concat(Enumerable.Repeat("body text about telemetry. ", 20)),
				Tags = ["memory", "telemetry"],
			}).ToList(), []);
	}

	[Fact]
	public async Task SearchRow_CapsDescription_FullTextStaysInMemoryGet()
	{
		await Seed(3);

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, _recorder, "телеметрию",
			scope: "project", store: "notes", bodyLen: 0);

		res.Items.Should().NotBeEmpty();
		foreach (var row in res.Items)
		{
			row.Description.Should().HaveLength(Cap + 1);            // 160 chars + the "…" marker
			row.Description.Should().EndWith("…");
			row.Description.Should().StartWith(LongDescription[..40]); // a prefix cut, not a rewrite
			row.Body.Should().BeNull();                                // bodyLen:0 — head-only rows
		}

		// The full description is not lost — the addressed read still carries it whole.
		var got = await MemoryTools.GetAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, _recorder, Proj, "notes", "row1");
		got.Entries.Should().ContainSingle().Which.Description.Should().Be(LongDescription);
	}

	[Fact]
	public async Task ListingRow_CapsDescription_Too()
	{
		await Seed(3);

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, _recorder,
			scope: "project", store: "notes", bodyLen: 0);

		res.Items.Should().NotBeEmpty();
		res.Items.Should().OnlyContain(i => i.Description.Length == Cap + 1 && i.Description.EndsWith('…'));
	}

	// The MEASUREMENT the cap exists for (baseline 2026-07-12: ~344 wire chars per row at
	// bodyLen:0 — a row with no body at all). "Before" is the same delivered row re-priced with
	// its description un-cut; "after" is what actually goes on the wire. ASCII text on purpose:
	// the wire form escapes non-ASCII as \uXXXX (6 chars each), which would multiply every number
	// by ~6 and make them incomparable with the production baseline.
	[Fact]
	public async Task RowWeight_AtBodyLenZero_DropsAgainstTheUncappedRow()
	{
		await SeedAscii(13); // the shape of the 2026-07-12 measurement: one query, 13 rows

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, _recorder, "telemetry",
			scope: "project", store: "notes", bodyLen: 0, limit: 20);

		res.Items.Should().HaveCount(13);
		var after = res.Items.Sum(ResponseBudget.CostOf);
		var before = res.Items.Sum(r => ResponseBudget.CostOf(r with { Description = AsciiDescription }));

		_output.WriteLine(
			$"rows={res.Items.Count} before={before} ({before / (double)res.Items.Count:F0}/row) " +
			$"after={after} ({after / (double)res.Items.Count:F0}/row)");

		after.Should().BeLessThan(before);
		(after / (double)res.Items.Count).Should().BeLessThan(before / (double)res.Items.Count);
		// A capped row's head is bounded: cap + key/type/tags/version/score/retriever envelope.
		(after / (double)res.Items.Count).Should().BeLessThan(340);
	}
}
