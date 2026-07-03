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

// W5.2 (honest cross-scope merge) + W6 (provenance surface) at the memory_search MCP adapter.
// The scope cascade (project ⊕ workspace) used to hand the limit greedily to the project leg and
// leave workspace the remainder; now both scopes compete on fused relevance so the best hit wins
// regardless of container. And each row carries a compact sourcesCount parsed from metadata.
[Collection("DataModule")]
public sealed class MemoryProvenanceMergeTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemoryProvenanceMergeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memprov-" + Guid.NewGuid().ToString("N"));
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

	// W5.2: a strong (rank-0) hit in the WORKSPACE scope must outrank a weak (rank-2) hit in the
	// PROJECT scope. Under the old greedy cascade every project row preceded every workspace row,
	// so the project-weak hit would sit above the workspace-strong one; the honest score merge
	// interleaves them by relevance.
	[Fact]
	public async Task CrossScope_StrongWorkspaceHit_OutranksWeakProjectHit()
	{
		var http = Http("memory:read,memory:write");
		// Project: two strong decoys bury the weak target at rank 2.
		await MemoryTools.RememberAsync(http, Flags(), _memory, "deploy deploy deploy release", scope: "project");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "deploy deploy pipeline", scope: "project");
		var pWeak = await MemoryTools.RememberAsync(http, Flags(), _memory, "a note that mentions deploy once", scope: "project");
		// Workspace: a single top-ranked hit.
		var wStrong = await MemoryTools.RememberAsync(http, Flags(), _memory, "deploy", scope: "workspace");

		var res = await MemoryTools.SearchAsync(http, Flags(), _memory, new NoopUsageRecorder(), "deploy");
		var keys = res.Items.Select(h => h.Key).ToList();

		keys.Should().Contain(wStrong.Key).And.Contain(pWeak.Key);
		res.Items.Single(h => h.Key == wStrong.Key).Scope.Should().Be("workspace");
		keys.IndexOf(wStrong.Key).Should().BeLessThan(keys.IndexOf(pWeak.Key),
			"the best hit wins regardless of container — workspace-strong beats project-weak");
	}

	// W5.2 corollary: when both scopes hold an EQUALLY relevant (rank-0) hit, the tie resolves
	// project-first — the documented cascade precedence is preserved by the honest merge.
	[Fact]
	public async Task CrossScope_EqualRelevance_ProjectWinsTie()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "kafka rebalance storm", scope: "project");
		await MemoryTools.RememberAsync(http, Flags(), _memory, "kafka rebalance storm", scope: "workspace");

		var res = await MemoryTools.SearchAsync(http, Flags(), _memory, new NoopUsageRecorder(), "kafka");
		res.Items.Select(h => h.Scope).Should().Equal("project", "workspace");
	}

	// W6: sourcesCount is the DISTINCT union of sessionId ∪ seenIn ∪ sources parsed from metadata —
	// a compact number only; an entry without session provenance omits it (null).
	[Fact]
	public async Task SourcesCount_CountsDistinctSessionProvenance_OmitsWhenAbsent()
	{
		var http = Http("memory:read,memory:write");
		var meta = JsonSerializer.Serialize(new
		{
			sessionId = "s1",
			seenIn = new[] { "s2", "s3" },
			sources = new[] { "s3", "s4" }, // s3 overlaps seenIn → union {s1,s2,s3,s4} = 4
		});
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "prov", type = "reference", description = "provenance fact", body = "kubernetes provenance note", metadata = meta },
			new { key = "plain", type = "project", description = "plain fact", body = "kubernetes plain note" },
		}));

		var res = await MemoryTools.SearchAsync(http, Flags(), _memory, new NoopUsageRecorder(), "kubernetes", scope: "project");
		res.Items.Single(h => h.Key == "prov").SourcesCount.Should().Be(4);
		res.Items.Single(h => h.Key == "plain").SourcesCount.Should().BeNull();
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
