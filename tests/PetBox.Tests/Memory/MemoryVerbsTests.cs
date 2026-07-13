using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Memory;

// The verbs memory_remember / memory_search: verbatim capture with a `scope` dimension
// (project default, workspace reserved), and the unified read (list = search without q)
// that cascades project ⊕ workspace, sweeps every store by default, and labels rows by
// scope. The "$workspace" container project is seeded by M028, so MigrationRunner makes
// it available here.
public sealed class MemoryVerbsTests : IDisposable
{
	const string Proj = "proj";
	const string OtherProj = "otherproj";
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
		// Proj lives in the SAME workspace as the seeded $workspace container (resolved, not
		// hardcoded) — the workspace-membership authz lets its keys curate the shared memory;
		// OtherProj lives in a foreign workspace and must be rejected there.
		var containerWs = _db.Projects.First(p => p.Key == MemoryTools.WorkspaceContainer).WorkspaceKey;
		_db.Insert(new Project { Key = Proj, WorkspaceKey = containerWs, Name = "P", Description = "" });
		_db.Insert(new Project { Key = OtherProj, WorkspaceKey = "other-ws", Name = "O", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db.Factory(), _factory);
		_memory = new MemoryService(_store);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Remember_DefaultsToProjectScope_TypeProject_AndSearchFindsIt()
	{
		var http = Http("memory:read,memory:write");
		var rem = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "api keys carry enumerable scopes");
		rem.Scope.Should().Be("project");
		rem.Store.Should().Be("notes");

		var rec = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "scopes");
		var hits = rec.Items.ToList();
		hits.Should().ContainSingle();
		hits[0].Scope.Should().Be("project");
		hits[0].Type.Should().Be("Project");
		hits[0].Body.Should().Contain("enumerable");
	}

	[Fact]
	public async Task Search_ReturnsVersion_ThatWorksAsUpsertBaseline()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "the deploy tag drives prod releases");

		var rec = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "releases");
		var hit = rec.Items.Single();
		var key = hit.Key;
		var version = hit.Version;
		version.Should().BeGreaterThan(0);

		// The recalled version is a valid per-key CAS baseline: the edit applies cleanly,
		// no Stale round-trip (the bug recall→upsert used to be doomed to).
		var entries = McpInputs.Entries(new object[]
		{
			new { key, type = "Project", description = "d", body = "edited body", version },
		});
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes", entries);
		res.Applied.Should().BeTrue();
		res.Conflicts.Should().BeEmpty();
		res.Updated.Select(e => e.Key)
			.Should().Contain(key);
	}

	[Fact]
	public async Task Upsert_PatchWithoutType_KeepsCurrentType()
	{
		// explicit-write-semantics / memory-upsert-patch-type-required: memory_upsert is PATCH,
		// so an omitted `type` on an EDIT must keep the current type — not be rejected. (Root
		// cause was ToEntry doing ParseType(i.Type) with no fallback to current?.Type, unlike the
		// neighbouring description/body/tags/metadata which all PATCH-fall back.)
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "the deploy tag drives prod releases", type: "Reference");

		var hit = (await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "releases")).Items.Single();
		hit.Type.Should().Be("Reference");

		// PATCH the body only — the entry carries NO `type`. Must apply and keep type=Reference.
		var entries = McpInputs.Entries(new object[]
		{
			new { key = hit.Key, body = "an edited marker body", version = hit.Version },
		});
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes", entries);
		res.Applied.Should().BeTrue();

		var after = (await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "edited")).Items.Single();
		after.Type.Should().Be("Reference"); // unchanged by the type-less PATCH
		after.Body.Should().Contain("edited marker");
	}

	[Fact]
	public async Task Upsert_NewEntryWithoutType_IsRejected()
	{
		// The PATCH fallback must NOT weaken create-semantics: a NEW entry (version 0) still
		// requires an explicit type.
		var http = Http("memory:read,memory:write");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "no-type-new", description = "d", body = "b", version = 0 },
		});
		var act = async () => await MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes", entries);
		await act.Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task Remember_Workspace_IsCrossProject_NotVisibleToProjectScope()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "the user prefers tabs over spaces",
			scope: "workspace", type: "User");

		// Cascade recall surfaces it, labelled workspace.
		var cascade = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "tabs");
		var wsHit = cascade.Items.Single(h => h.Scope == "workspace");
		wsHit.Type.Should().Be("User");

		// Project-scoped recall must NOT see workspace memory.
		var projOnly = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "tabs", scope: "project");
		projOnly.Items.Should().BeEmpty();
	}

	[Fact]
	public async Task Search_Cascade_ListsProjectBeforeWorkspace()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "deploy moves the deploy tag", scope: "project");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "deploy needs CI health gate", scope: "workspace");

		var rec = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "deploy");
		var scopes = rec.Items.Select(h => h.Scope).ToList();
		scopes.Should().Equal("project", "workspace"); // project leg first
	}

	[Fact]
	public async Task Search_SearchesEveryStoreByDefault()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "alpha lives in notes", store: "notes");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "alpha lives in journal", store: "journal");

		var rec = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "alpha");
		var stores = rec.Items.Select(h => h.Store).ToList();
		stores.Should().BeEquivalentTo(["notes", "journal"]);

		// store narrows to one.
		var narrowed = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "alpha", store: "journal");
		narrowed.Items.Select(h => h.Store)
			.Should().BeEquivalentTo(["journal"]);
	}

	[Fact]
	public async Task Search_AllStores_SkipsSensitiveOps_ButExplicitStoreReaches()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "secret deploy token xyz", store: "ops");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "public deploy note", store: "notes");

		// Implicit all-stores sweep must NOT surface the ops store.
		var sweep = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "deploy");
		sweep.Items.Select(h => h.Store)
			.Should().NotContain("ops").And.Contain("notes");

		// Explicit store:ops is a deliberate ask and still reaches it.
		var explicitOps = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "deploy", store: "ops");
		explicitOps.Items.Select(h => h.Store)
			.Should().BeEquivalentTo(["ops"]);
	}

	// System stores (spec: memoverhaul store taxonomy) are machine plumbing — the implicit
	// sweep excludes them like ops, but an explicit store: still reaches them.
	[Fact]
	public async Task Search_AllStores_SkipsSystemStores_ButExplicitStoreReaches()
	{
		var http = Http("memory:read,memory:write");
		// "session-digests" is a well-known system store (MemoryStore.SystemStoreNames) — the
		// auto-vivify write path marks it IsSystem even though nothing called CreateStoreAsync.
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "digest of prior sessions", store: "session-digests");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "public note about sessions", store: "notes");

		// Implicit all-stores sweep must NOT surface the system store.
		var sweep = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "sessions");
		sweep.Items.Select(h => h.Store)
			.Should().NotContain("session-digests").And.Contain("notes");

		// Explicit store:session-digests is a deliberate ask and still reaches it.
		var explicitSys = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "sessions", store: "session-digests");
		explicitSys.Items.Select(h => h.Store)
			.Should().BeEquivalentTo(["session-digests"]);
	}

	// Sweep-exclusion is DECOUPLED from the IsSystem badge (card ui-memory-system-store-widen):
	// `canon` and `autocaptured` are protected system stores but ARE knowledge, so the implicit
	// all-stores sweep KEEPS returning them — only `session-digests` (a summary index) is excluded.
	[Fact]
	public async Task Search_AllStores_KeepsCanonAndAutocaptured_ExcludesOnlySessionDigests()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "pelican fact for canon", store: "canon");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "pelican fact autocaptured", store: "autocaptured");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "pelican fact in notes", store: "notes");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "pelican fact digest", store: "session-digests");

		var sweep = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), "pelican");
		var stores = sweep.Items.Select(h => h.Store).ToList();
		stores.Should().Contain("canon").And.Contain("autocaptured").And.Contain("notes");
		stores.Should().NotContain("session-digests");
	}

	// list = search without q (uniform-entity-verbs v2): the listing cascades the same
	// containers as a query, labels rows by scope, and defaults to updated desc.
	[Fact]
	public async Task Search_WithoutQ_ListsCascade_UpdatedDescDefault()
	{
		var http = Http("memory:read,memory:write");
		var first = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "older fact");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "workspace fact", scope: "workspace");
		await Task.Delay(30); // distinct Updated timestamps — the listing orders by them
		var second = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "newer fact");

		var res = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder());
		res.Retrievers.Should().BeNull(); // no retriever runs in listing mode
		res.Items.Select(h => h.Scope).Distinct().Should().BeEquivalentTo(["project", "workspace"]);

		// Project leg first; within it the freshest write leads (updated desc).
		var proj = res.Items.Where(h => h.Scope == "project").Select(h => h.Key).ToList();
		proj.IndexOf(second.Key).Should().BeLessThan(proj.IndexOf(first.Key));
	}

	[Fact]
	public async Task Search_SortRelevanceWithoutQ_IsRejected()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "any fact");
		var act = () => MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			sort: new PetBox.Web.Mcp.Contract.SortInput { By = "relevance" });
		(await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("relevance");
	}

	// With q an explicit created/updated sort reorders WITHIN the relevance-selected set.
	[Fact]
	public async Task Search_WithQ_ExplicitSort_ReordersSelected()
	{
		var http = Http("memory:read,memory:write");
		var a = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "gamma fact one");
		await Task.Delay(30); // distinct Created timestamps — the sort reads them
		var b = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "gamma fact two");

		var res = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			"gamma", scope: "project", sort: new PetBox.Web.Mcp.Contract.SortInput { By = "created", Desc = true });
		res.Retrievers.Should().NotBeNull();
		var keys = res.Items.Select(h => h.Key).ToList();
		keys.Should().BeEquivalentTo([a.Key, b.Key]);
		keys.IndexOf(b.Key).Should().BeLessThan(keys.IndexOf(a.Key)); // created desc — newest first
	}

	[Fact]
	public async Task Remember_InvalidScope_IsRejected()
	{
		var http = Http("memory:read,memory:write");
		await Assert.ThrowsAsync<ArgumentException>(() => MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "x", scope: "galaxy"));
	}

	[Fact]
	public async Task Remember_RequiresWriteScope()
	{
		var http = Http("memory:read");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "x"));
	}

	// workspace-memory-authz-fix: the reserved $workspace container feeds every project's
	// cascade, so KEY-ADDRESSED curation (memory_upsert/get/…) must reach it too — not only
	// the scope:"workspace" verbs. A project-scoped key (claim = Proj, which lives in the
	// container's own workspace, not "*") may address it directly.
	[Fact]
	public async Task Upsert_WorkspaceContainer_ByProjectScopedKey_Succeeds()
	{
		var http = Http("memory:read,memory:write");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "index", type = "Project", description = "canon index", body = "the workspace canon", version = 0 },
		});
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, MemoryTools.WorkspaceContainer, "canon", entries);
		res.Applied.Should().BeTrue();
		res.Added.Select(e => e.Key).Should().Contain("index");
	}

	[Fact]
	public async Task Get_WorkspaceContainer_ByProjectScopedKey_Succeeds()
	{
		var http = Http("memory:read,memory:write");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "index", type = "Project", description = "canon index", body = "the workspace canon", version = 0 },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, MemoryTools.WorkspaceContainer, "canon", entries);

		var got = await MemoryTools.GetAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(),
			MemoryTools.WorkspaceContainer, "canon", "index");
		got.Entries.Should().ContainSingle();
		got.Entries[0].Body.Should().Contain("workspace canon");
	}

	// The write scope still gates the workspace pass: waving $workspace through the project
	// assert does NOT bypass memory:write.
	[Fact]
	public async Task Upsert_WorkspaceContainer_WithoutWriteScope_IsRejected()
	{
		var http = Http("memory:read");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "index", type = "Project", description = "d", body = "b", version = 0 },
		});
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, MemoryTools.WorkspaceContainer, "canon", entries));
	}

	// Multi-workspace boundary: a key whose project lives in a DIFFERENT workspace than the
	// $workspace container must NOT reach the shared memory of this one.
	[Fact]
	public async Task Upsert_WorkspaceContainer_ByForeignWorkspaceKey_IsRejected()
	{
		var http = Http("memory:read,memory:write", OtherProj);
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "index", type = "Project", description = "d", body = "b", version = 0 },
		});
		var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, MemoryTools.WorkspaceContainer, "canon", entries));
		ex.Message.Should().Contain("workspace");
	}

	// AssertProject (tasks/sessions/etc.) is unchanged: a project-scoped key may NOT address
	// $workspace as a project — only the memory surface's AssertMemoryProjectAsync grants it.
	[Fact]
	public async Task NonMemoryTool_AddressingWorkspaceContainer_StillFails()
	{
		var http = Http("memory:read,memory:write");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			ModuleMcp.AssertProject(http, MemoryTools.WorkspaceContainer));
	}

	// workspace-memory-isolation: remembering INTO the $system "$workspace" container is
	// denied for a key whose project lives in a foreign workspace (same boundary as upsert).
	[Fact]
	public async Task Remember_Workspace_ForeignWorkspace_Denied()
	{
		var http = Http("memory:read,memory:write", OtherProj);
		var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "foreign leak",
				scope: "workspace", projectKey: MemoryTools.WorkspaceContainer));
		ex.Message.Should().Contain("workspace");
	}

	// Two workspaces, two containers: cascade must use the caller's own workspace container
	// — never a foreign one, and never a hard-coded global for a non-$system caller.
	// Note: HttpContextAccessor uses a static AsyncLocal — create each accessor just before
	// the call (never hold two live ones), or the second set overwrites the first claim.
	[Fact]
	public async Task Search_Cascade_UsesCallerWorkspace_NotForeignOrGlobal()
	{
		var sysRem = await MemoryTools.RememberAsync(Http("memory:read,memory:write", Proj), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			"alpha isolation marker from system-ws", scope: "workspace");
		var otherRem = await MemoryTools.RememberAsync(Http("memory:read,memory:write", OtherProj), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			"beta isolation marker from other-ws", scope: "workspace");

		// Ground truth: each caller's workspace container is distinct.
		sysRem.Id.Should().StartWith(MemoryTools.WorkspaceContainer + "/");
		otherRem.Id.Should().StartWith("$ws-other-ws/");
		_db.Projects.Any(p => p.Key == "$ws-other-ws" && p.WorkspaceKey == "other-ws").Should().BeTrue();

		var sysHits = (await MemoryTools.SearchAsync(Http("memory:read,memory:write", Proj), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			new NoopUsageRecorder(), "isolation marker")).Items.ToList();
		sysHits.Should().Contain(h => h.Body != null && h.Body.Contains("alpha"));
		sysHits.Should().NotContain(h => h.Body != null && h.Body.Contains("beta"));

		var otherHits = (await MemoryTools.SearchAsync(Http("memory:read,memory:write", OtherProj), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			new NoopUsageRecorder(), "isolation marker")).Items.ToList();
		otherHits.Should().Contain(h => h.Body != null && h.Body.Contains("beta"));
		otherHits.Should().NotContain(h => h.Body != null && h.Body.Contains("alpha"));
	}

	// $system keeps the legacy "$workspace" container key (zero-migration for prod data).
	[Fact]
	public async Task Remember_Workspace_SystemWorkspace_UsesLegacyContainer()
	{
		var http = Http("memory:read,memory:write", Proj);
		var rem = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory,
			"system workspace lands in legacy container", scope: "workspace");
		rem.Id.Should().StartWith(MemoryTools.WorkspaceContainer + "/");
	}

	// Non-$system workspace resolves to the "$ws-" prefix and lazy-ensures the Projects row.
	[Fact]
	public async Task Remember_Workspace_NonSystem_ResolvesToWsPrefix()
	{
		var http = Http("memory:read,memory:write", OtherProj);
		var rem = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory,
			"non-system workspace container", scope: "workspace");
		rem.Id.Should().StartWith("$ws-other-ws/");
		var row = _db.Projects.First(p => p.Key == "$ws-other-ws");
		row.WorkspaceKey.Should().Be("other-ws");
	}

	// Wildcard "*" may write the $system "$workspace" container; a foreign project-scoped
	// key may not (covered by Upsert_WorkspaceContainer_ByForeignWorkspaceKey_IsRejected).
	[Fact]
	public async Task Upsert_WorkspaceContainer_ByWildcardKey_Succeeds()
	{
		var http = Http("memory:read,memory:write", "*");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "star-index", type = "Project", description = "d", body = "wildcard write", version = 0 },
		});
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, MemoryTools.WorkspaceContainer, "canon", entries);
		res.Applied.Should().BeTrue();
		res.Added.Select(e => e.Key).Should().Contain("star-index");
	}

	// Bare cascade with a "*" key and no projectKey: both legs are unresolvable → degrade
	// to an empty result (admin/wiring bare memory_search("q")), not ArgumentException.
	// Explicit scope=workspace still requires a resolvable project and throws.
	[Fact]
	public async Task Search_WildcardWithoutProjectKey_CascadeDegradesToEmpty()
	{
		var http = Http("memory:read,memory:write", "*");
		var res = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(), "anything");
		res.Items.Should().BeEmpty();

		await Assert.ThrowsAsync<ArgumentException>(() =>
			MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(), "anything",
				scope: "workspace"));
	}

	// First workspace-scope read before any write: container is lazy-ensured, search returns
	// empty (no error) — critical path for a fresh non-$system workspace.
	[Fact]
	public async Task Search_Workspace_BeforeAnyWrite_EmptyWithoutError()
	{
		var http = Http("memory:read", OtherProj);
		var res = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(),
			scope: "workspace");
		res.Items.Should().BeEmpty();
		_db.Projects.Any(p => p.Key == "$ws-other-ws" && p.WorkspaceKey == "other-ws").Should().BeTrue();
	}

	IHttpContextAccessor Http(string scopes, string project = Proj)
	{
		var id = new ClaimsIdentity([new Claim("project", project), new Claim("scopes", scopes)], "test");
		// ModuleMcp.AssertProject/ResolveProject resolve IProjectCatalog off the HttpContext's own
		// DI container (spec work/smoke-writes-into-real-projects).
		var services = new ServiceCollection().AddSingleton<IProjectCatalog>(new ProjectCatalog(_db.Factory())).BuildServiceProvider();
		return new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id), RequestServices = services },
		};
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
