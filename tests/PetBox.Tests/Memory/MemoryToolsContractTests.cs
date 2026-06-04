using System.Security.Claims;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Memory;

// Verifies the A4 taxonomy contract on the Memory MCP surface: type is required on
// upsert, list/search filter by type, tags normalise, and the store auto-vivifies
// on a cold upsert (A3, mirrored for memory).
[Collection("DataModule")]
public sealed class MemoryToolsContractTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemoryToolsContractTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memtools-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
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

	[Fact]
	public async Task Upsert_WithoutType_IsRejected()
	{
		var http = Http("memory:read,memory:write");
		var entries = JsonSerializer.SerializeToElement(new object[]
		{
			new { key = "k", description = "d", body = "b" },
		});
		// GuardAsync surfaces the missing-type validation as a structured error result.
		var res = Json(await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries));
		res.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
	}

	[Fact]
	public async Task Upsert_AutoVivifies_NormalisesTags_AndFiltersByType()
	{
		var http = Http("memory:read,memory:write");

		// Cold upsert (no store_create) auto-creates the store; tags get normalised.
		var entries = JsonSerializer.SerializeToElement(new object[]
		{
			new { key = "go-style", type = "reference", description = "Go", body = "tabs", tags = "Go, STYLE ,go" },
			new { key = "prefers-tabs", type = "feedback", description = "tabs", body = "user likes tabs" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries);
		(await _store.ExistsAsync(Proj, "notes")).Should().BeTrue();

		// Tags normalised: lowercased, trimmed, de-duped.
		var all = Json(await MemoryTools.ListAsync(http, Flags(), _memory, Proj, "notes"));
		var go = all.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("key").GetString() == "go-style");
		go.GetProperty("tags").GetString().Should().Be("go,style");
		go.GetProperty("type").GetString().Should().Be("Reference");

		// Type filter narrows the listing.
		var feedback = Json(await MemoryTools.ListAsync(http, Flags(), _memory, Proj, "notes", "feedback"));
		var keys = feedback.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
		keys.Should().BeEquivalentTo(["prefers-tabs"]);
	}

	[Fact]
	public async Task Search_Fts_FindsByToken_AndExcludesOthers()
	{
		var http = Http("memory:read,memory:write");
		var entries = JsonSerializer.SerializeToElement(new object[]
		{
			new { key = "auth-scopes", type = "project", description = "API key scopes", body = "scopes are enumerable, not wildcards" },
			new { key = "go-style", type = "reference", description = "Go conventions", body = "use tabs not spaces" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries);

		var res = Json(await MemoryTools.SearchAsync(http, Flags(), _memory, Proj, "notes", "scope"));
		var keys = res.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
		keys.Should().Contain("auth-scopes");      // "scope*" prefix-matches "scopes"
		keys.Should().NotContain("go-style");
	}

	[Fact]
	public async Task Upsert_AcceptsEntriesAsJsonString()
	{
		var http = Http("memory:read,memory:write");
		// Real MCP clients pass the untyped `entries` param as a JSON *string* (D6).
		var arrayJson = """[{"key":"k","type":"project","description":"d","body":"b"}]""";
		var entriesAsString = JsonSerializer.SerializeToElement(arrayJson); // ValueKind == String
		var res = Json(await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "strstore", entriesAsString));
		res.GetProperty("added").EnumerateArray().Should().ContainSingle()
			.Which.GetProperty("key").GetString().Should().Be("k");
	}

	[Fact]
	public async Task Upsert_DeletedTrue_SoftRemovesEntry()
	{
		var http = Http("memory:read,memory:write");
		var entries = JsonSerializer.SerializeToElement(new object[]
		{
			new { key = "temp", type = "project", description = "temp", body = "to be removed" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries);

		var del = JsonSerializer.SerializeToElement(new object[] { new { key = "temp", deleted = true } });
		var res = Json(await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", del));
		res.GetProperty("removed").EnumerateArray().Select(e => e.GetString()).Should().Contain("temp");

		var list = Json(await MemoryTools.ListAsync(http, Flags(), _memory, Proj, "notes"));
		list.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString())
			.Should().NotContain("temp");
	}

	// spec echo-compact-by-default (mirror of the tasks side): memory.upsert echoes
	// key/type/description/version but NOT the body unless bodyLen > 0. description (a one-liner)
	// stays to orient the merge; the heavy body is opt-in.
	[Fact]
	public async Task Upsert_EchoOmitsBodyByDefault_SlicesWithBodyLen()
	{
		var http = Http("memory:read,memory:write");
		var big = new string('y', 500);

		// Default echo: description present, body sliced to null.
		var entries = JsonSerializer.SerializeToElement(new object[]
		{
			new { key = "k", type = "project", description = "one-liner", body = big },
		});
		var added = Json(await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries))
			.GetProperty("added").EnumerateArray().Single();
		added.GetProperty("description").GetString().Should().Be("one-liner");
		added.GetProperty("body").ValueKind.Should().Be(JsonValueKind.Null);

		// bodyLen > 0: opt-in sliced body — first N chars + "…" when cut.
		var entries2 = JsonSerializer.SerializeToElement(new object[]
		{
			new { key = "k2", type = "project", description = "d", body = big },
		});
		var sliced = Json(await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries2, bodyLen: 300))
			.GetProperty("added").EnumerateArray().Single(e => e.GetProperty("key").GetString() == "k2")
			.GetProperty("body").GetString()!;
		sliced.Length.Should().Be(301);
		sliced.Should().EndWith("…");
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	// Mirror the MCP boundary (camelCase policy) so typed-record results read like live JSON.
	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, CamelCase);
}
