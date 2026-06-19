using System.Security.Claims;
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
		TestSchema.Core(cs); // seeds $system + $workspace projects
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
		var rem = await MemoryTools.RememberAsync(http, Flags(), _memory, "api keys carry enumerable scopes");
		rem.Scope.Should().Be("project");
		rem.Store.Should().Be("notes");

		var rec = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "scopes");
		var hits = rec.Results.ToList();
		hits.Should().ContainSingle();
		hits[0].Scope.Should().Be("project");
		hits[0].Type.Should().Be("Project");
		hits[0].Body.Should().Contain("enumerable");
	}

	[Fact]
	public async Task Recall_ReturnsVersion_ThatWorksAsUpsertBaseline()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "the deploy tag drives prod releases");

		var rec = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "releases");
		var hit = rec.Results.Single();
		var key = hit.Key;
		var version = hit.Version;
		version.Should().BeGreaterThan(0);

		// The recalled version is a valid per-key CAS baseline: the edit applies cleanly,
		// no Stale round-trip (the bug recall→upsert used to be doomed to).
		var entries = McpInputs.Entries(new object[]
		{
			new { key, type = "Project", description = "d", body = "edited body", version },
		});
		var res = await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries);
		res.Applied.Should().BeTrue();
		res.Conflicts.Should().BeEmpty();
		res.Updated.Select(e => e.Key)
			.Should().Contain(key);
	}

	[Fact]
	public async Task Remember_Workspace_IsCrossProject_NotVisibleToProjectScope()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "the user prefers tabs over spaces",
			scope: "workspace", type: "User");

		// Cascade recall surfaces it, labelled workspace.
		var cascade = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "tabs");
		var wsHit = cascade.Results.Single(h => h.Scope == "workspace");
		wsHit.Type.Should().Be("User");

		// Project-scoped recall must NOT see workspace memory.
		var projOnly = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "tabs", scope: "project");
		projOnly.Results.Should().BeEmpty();
	}

	[Fact]
	public async Task Recall_Cascade_ListsProjectBeforeWorkspace()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "deploy moves the deploy tag", scope: "project");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "deploy needs CI health gate", scope: "workspace");

		var rec = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "deploy");
		var scopes = rec.Results.Select(h => h.Scope).ToList();
		scopes.Should().Equal("project", "workspace"); // project leg first
	}

	[Fact]
	public async Task Recall_SearchesEveryStoreByDefault()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "alpha lives in notes", store: "notes");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "alpha lives in journal", store: "journal");

		var rec = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "alpha");
		var stores = rec.Results.Select(h => h.Store).ToList();
		stores.Should().BeEquivalentTo(["notes", "journal"]);

		// store narrows to one.
		var narrowed = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "alpha", store: "journal");
		narrowed.Results.Select(h => h.Store)
			.Should().BeEquivalentTo(["journal"]);
	}

	[Fact]
	public async Task Recall_AllStores_SkipsSensitiveOps_ButExplicitStoreReaches()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "secret deploy token xyz", store: "ops");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "public deploy note", store: "notes");

		// Implicit all-stores sweep must NOT surface the ops store.
		var sweep = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "deploy");
		sweep.Results.Select(h => h.Store)
			.Should().NotContain("ops").And.Contain("notes");

		// Explicit store:ops is a deliberate ask and still reaches it.
		var explicitOps = await MemoryTools.RecallAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "deploy", store: "ops");
		explicitOps.Results.Select(h => h.Store)
			.Should().BeEquivalentTo(["ops"]);
	}

	[Fact]
	public async Task Remember_InvalidScope_IsRejected()
	{
		var http = Http("memory:read,memory:write");
		await Assert.ThrowsAsync<ArgumentException>(() => MemoryTools.RememberAsync(http, Flags(), _memory, "x", scope: "galaxy"));
	}

	[Fact]
	public async Task Remember_RequiresWriteScope()
	{
		var http = Http("memory:read");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MemoryTools.RememberAsync(http, Flags(), _memory, "x"));
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
}
