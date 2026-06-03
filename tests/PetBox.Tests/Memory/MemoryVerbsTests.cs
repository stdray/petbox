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

// The ergonomic verbs memory.remember / memory.recall: verbatim capture with a `scope`
// dimension (project default, workspace reserved), and recall that cascades project ⊕
// workspace, searches every store by default, and labels hits by scope. The "$workspace"
// container project is seeded by M028, so MigrationRunner makes it available here.
[Collection("DataModule")]
public sealed class MemoryVerbsTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemoryVerbsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memverbs-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs); // seeds $system + $workspace projects
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
	public async Task Remember_DefaultsToProjectScope_TypeProject_AndRecallFindsIt()
	{
		var http = Http("memory:read,memory:write");
		var rem = Json(await MemoryTools.RememberAsync(http, Flags(), _memory, "api keys carry enumerable scopes"));
		rem.GetProperty("scope").GetString().Should().Be("project");
		rem.GetProperty("store").GetString().Should().Be("notes");

		var rec = Json(await MemoryTools.RecallAsync(http, Flags(), _memory, "scopes"));
		var hits = rec.GetProperty("results").EnumerateArray().ToList();
		hits.Should().ContainSingle();
		hits[0].GetProperty("scope").GetString().Should().Be("project");
		hits[0].GetProperty("type").GetString().Should().Be("Project");
		hits[0].GetProperty("body").GetString().Should().Contain("enumerable");
	}

	[Fact]
	public async Task Remember_Workspace_IsCrossProject_NotVisibleToProjectScope()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "the user prefers tabs over spaces",
			scope: "workspace", type: "User");

		// Cascade recall surfaces it, labelled workspace.
		var cascade = Json(await MemoryTools.RecallAsync(http, Flags(), _memory, "tabs"));
		var wsHit = cascade.GetProperty("results").EnumerateArray()
			.Single(h => h.GetProperty("scope").GetString() == "workspace");
		wsHit.GetProperty("type").GetString().Should().Be("User");

		// Project-scoped recall must NOT see workspace memory.
		var projOnly = Json(await MemoryTools.RecallAsync(http, Flags(), _memory, "tabs", scope: "project"));
		projOnly.GetProperty("results").EnumerateArray().Should().BeEmpty();
	}

	[Fact]
	public async Task Recall_Cascade_ListsProjectBeforeWorkspace()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "deploy moves the deploy tag", scope: "project");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "deploy needs CI health gate", scope: "workspace");

		var rec = Json(await MemoryTools.RecallAsync(http, Flags(), _memory, "deploy"));
		var scopes = rec.GetProperty("results").EnumerateArray()
			.Select(h => h.GetProperty("scope").GetString()).ToList();
		scopes.Should().Equal("project", "workspace"); // project leg first
	}

	[Fact]
	public async Task Recall_SearchesEveryStoreByDefault()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "alpha lives in notes", store: "notes");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "alpha lives in journal", store: "journal");

		var rec = Json(await MemoryTools.RecallAsync(http, Flags(), _memory, "alpha"));
		var stores = rec.GetProperty("results").EnumerateArray()
			.Select(h => h.GetProperty("store").GetString()).ToList();
		stores.Should().BeEquivalentTo(["notes", "journal"]);

		// store narrows to one.
		var narrowed = Json(await MemoryTools.RecallAsync(http, Flags(), _memory, "alpha", store: "journal"));
		narrowed.GetProperty("results").EnumerateArray().Select(h => h.GetProperty("store").GetString())
			.Should().BeEquivalentTo(["journal"]);
	}

	[Fact]
	public async Task Recall_AllStores_SkipsSensitiveOps_ButExplicitStoreReaches()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "secret deploy token xyz", store: "ops");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "public deploy note", store: "notes");

		// Implicit all-stores sweep must NOT surface the ops store.
		var sweep = Json(await MemoryTools.RecallAsync(http, Flags(), _memory, "deploy"));
		sweep.GetProperty("results").EnumerateArray().Select(h => h.GetProperty("store").GetString())
			.Should().NotContain("ops").And.Contain("notes");

		// Explicit store:ops is a deliberate ask and still reaches it.
		var explicitOps = Json(await MemoryTools.RecallAsync(http, Flags(), _memory, "deploy", store: "ops"));
		explicitOps.GetProperty("results").EnumerateArray().Select(h => h.GetProperty("store").GetString())
			.Should().BeEquivalentTo(["ops"]);
	}

	[Fact]
	public async Task Remember_InvalidScope_IsRejected()
	{
		var http = Http("memory:read,memory:write");
		var res = Json(await MemoryTools.RememberAsync(http, Flags(), _memory, "x", scope: "galaxy"));
		res.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
	}

	[Fact]
	public async Task Remember_RequiresWriteScope()
	{
		var http = Http("memory:read");
		var res = Json(await MemoryTools.RememberAsync(http, Flags(), _memory, "x"));
		res.GetProperty("error").GetProperty("type").GetString().Should().Be("UnauthorizedAccessException");
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
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, CamelCase);
}
