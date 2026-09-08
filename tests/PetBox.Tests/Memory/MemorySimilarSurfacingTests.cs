using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Memory;

// Card canon-promises-dedup-mechanism-does-not-give: the deterministic dedup guards
// (AutocaptureDedup / ObservationDedupService) match on title+body IDENTITY plus a cosine
// cutoff computed over the WHOLE concatenated text — BY DESIGN, so genuinely different facts
// are never silently merged. That design also lets a close-but-differently-worded entry pass
// the gate as "new". Rather than widening the dedup gate itself (explicitly out of scope —
// the DECIDED move), memory_upsert/memory_remember now SURFACE the top-3 nearest existing
// entries in their echo (`Similar`/`SimilarEntriesView`) so a human/agent decides — and do so
// HONESTLY when the embedder (llm_embed, home-PC priority-10 route) is degraded: an empty list
// with `Unavailable:true`, never a false "no duplicates" from a silent skip.
public sealed class MemorySimilarSurfacingTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemorySimilarSurfacingTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memsimilar-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_store = new MemoryStore(_db.Factory(), _factory);
		_memory = new MemoryService(_store);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// The headline behavior: a fresh write that near-duplicates an existing entry is still
	// CREATED (never absorbed/blocked) but comes back naming the nearest neighbor.
	[Fact]
	public async Task Upsert_NearDuplicateOfExisting_SurfacesItInSimilar()
	{
		var fake = new FakeLlmClient();
		await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e1", type = "project", description = $"first {FakeLlmClient.NearQueryMarker}", body = "b1" } }),
			client: fake);

		var res = await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e2", type = "project", description = $"second {FakeLlmClient.NearQueryMarker}", body = "b2" } }),
			client: fake);

		res.Applied.Should().BeTrue();
		res.Similar.Should().NotBeNull();
		var row = res.Similar!.Should().ContainSingle(s => s.Key == "e2").Subject;
		row.Unavailable.Should().BeFalse();
		row.Entries.Should().NotBeNull();
		row.Entries!.Should().ContainSingle(e => e.Key == "e1");
	}

	// The write is never absorbed — the new entry is genuinely created even though a near
	// twin is surfaced right next to it.
	[Fact]
	public async Task Upsert_NearDuplicateOfExisting_StillCreatesTheNewEntry()
	{
		var fake = new FakeLlmClient();
		await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e1", type = "project", description = $"first {FakeLlmClient.NearQueryMarker}", body = "b1" } }),
			client: fake);
		var res = await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e2", type = "project", description = $"second {FakeLlmClient.NearQueryMarker}", body = "b2" } }),
			client: fake);

		res.Added.Should().ContainSingle(e => e.Key == "e2");
	}

	// No other entries in the store: the check ran (conceptually) but had nothing to compare
	// against — an honest empty, NOT the degraded "unavailable" state.
	[Fact]
	public async Task Upsert_FirstEntryInStore_EmptySimilarNotUnavailable()
	{
		var res = await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "solo", type = "project", description = "d", body = "b" } }),
			client: new FakeLlmClient());

		var row = res.Similar!.Should().ContainSingle(s => s.Key == "solo").Subject;
		row.Unavailable.Should().BeFalse();
		row.Entries.Should().BeEmpty();
	}

	// Embedder unreachable (llm_embed degraded — the home-PC priority-10 route the owner asked
	// to be checked): the check must NOT silently read as "no duplicates". Entries stays empty
	// but Unavailable flips true — an explicit "don't know".
	[Fact]
	public async Task Upsert_EmbedderThrows_MarksSimilarityUnavailable_NotEmptyNoDupes()
	{
		await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e1", type = "project", description = "d1", body = "b1" } }));

		var res = await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e2", type = "project", description = "d2", body = "b2" } }),
			client: new ThrowingLlmClient());

		var row = res.Similar!.Should().ContainSingle(s => s.Key == "e2").Subject;
		row.Unavailable.Should().BeTrue();
		row.Entries.Should().BeEmpty();
	}

	// No `client` argument at all (the MCP default when the router has no route configured):
	// same honest-unavailable answer, not a crash and not a false negative.
	[Fact]
	public async Task Upsert_NoClientProvided_MarksSimilarityUnavailable()
	{
		await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e1", type = "project", description = "d1", body = "b1" } }));

		var res = await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e2", type = "project", description = "d2", body = "b2" } }));

		var row = res.Similar!.Should().ContainSingle(s => s.Key == "e2").Subject;
		row.Unavailable.Should().BeTrue();
		row.Entries.Should().BeEmpty();
	}

	// A sensitive store (MemoryStores.IsSensitive — "ops") must never send its text to the
	// outbound embedder: the check is skipped entirely (Entries: null, not []) rather than
	// degrading — proven here by handing it an embedder that THROWS on any call and observing
	// no throw propagates and no comparison is reported as having been attempted.
	[Fact]
	public async Task Upsert_SensitiveStore_SkipsSimilarityCheckEntirely()
	{
		await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "ops",
			McpInputs.Entries(new object[] { new { key = "s1", type = "project", description = "secret one", body = "b1" } }));

		var res = await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "ops",
			McpInputs.Entries(new object[] { new { key = "s2", type = "project", description = "secret two", body = "b2" } }),
			client: new ThrowingLlmClient());

		var row = res.Similar!.Should().ContainSingle(s => s.Key == "s2").Subject;
		row.Entries.Should().BeNull();
		row.Unavailable.Should().BeFalse();
	}

	// Updates/deletes never carry similarity — only a genuinely NEW key does (the risk this
	// surfaces is specifically "a fresh write duplicates something", not an edit of itself).
	[Fact]
	public async Task Upsert_EditOnly_NoSimilarRows()
	{
		var created = (await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "k", type = "project", description = "d", body = "b" } }))).Added.Single();

		var res = await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "k", type = "project", body = "b2", version = created.Version } }));

		res.Added.Should().BeEmpty();
		res.Updated.Should().ContainSingle();
		res.Similar.Should().BeNull();
	}

	// memory_remember: the SAME show-don't-absorb surfacing, on the single-entry verb.
	[Fact]
	public async Task Remember_NearDuplicateOfExisting_SurfacesItAndStillCreates()
	{
		var fake = new FakeLlmClient();
		await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e1", type = "project", description = $"first {FakeLlmClient.NearQueryMarker}", body = "b1" } }),
			client: fake);

		var res = await MemoryTools.RememberAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			text: $"second {FakeLlmClient.NearQueryMarker}", projectKey: Proj, store: "notes", description: $"second {FakeLlmClient.NearQueryMarker}",
			client: fake);

		res.SimilarityUnavailable.Should().BeFalse();
		res.Similar.Should().NotBeNull();
		res.Similar!.Should().ContainSingle(e => e.Key == "e1");
		// The write itself is unconditional: the key really was created despite the match.
		var written = await _memory.GetAsync(Proj, "notes", res.Key, default);
		written.Should().NotBeNull();
	}

	[Fact]
	public async Task Remember_NoClientProvided_MarksSimilarityUnavailable()
	{
		await MemoryTools.UpsertAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new object[] { new { key = "e1", type = "project", description = "d1", body = "b1" } }));

		var res = await MemoryTools.RememberAsync(Http("memory:read,memory:write"), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			text: "some other fact", projectKey: Proj, store: "notes", description: "d2");

		res.SimilarityUnavailable.Should().BeTrue();
		res.Similar.Should().BeEmpty();
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) } };
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
}
