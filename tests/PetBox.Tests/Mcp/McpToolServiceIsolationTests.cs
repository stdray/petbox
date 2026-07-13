using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;
using PetBox.Data.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// The MCP tools stopped opening core.db and now ask a SERVICE (db-access-layer-cleanup). The services
// were written for a cookie USER; an MCP caller is a KEY with a project claim and a scope set. This
// file is the proof that the move cost nothing on the only two axes that matter on an external surface:
//
//   1. PROJECT ISOLATION — a key claiming project A reaches NOTHING of project B through the
//      translated tools (db_*, health_search, memory_*). It is the IDOR class we spent a day closing
//      in the UI: a service that takes a projectKey must treat it as an ADDRESS, so a foreign one
//      simply finds nothing, and the claim check must still run BEFORE the service is asked at all.
//   2. SCOPE — the provisioning verbs still demand admin:provision. The services carry no scope check
//      of their own (they cannot: they serve pages too), so a tool that forgot its AssertScope would
//      hand a data:read key the whole provisioning surface. These tests fail if one ever does.
public sealed class McpToolServiceIsolationTests : IDisposable
{
	const string Ws = "ws-a";
	const string OtherWs = "ws-b";
	const string ProjA = "proja";
	const string ProjB = "projb";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly IDataDbFactory _dataDbs;

	public McpToolServiceIsolationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mcpiso-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_dataDbs = new DataDbFactory(Path.Combine(_dir, "db"));

		_db.Insert(new Workspace { Key = Ws, Name = Ws, Description = "", CreatedAt = DateTime.UtcNow });
		_db.Insert(new Workspace { Key = OtherWs, Name = OtherWs, Description = "", CreatedAt = DateTime.UtcNow });
		_db.Insert(new Project { Key = ProjA, WorkspaceKey = Ws, Name = "A", Description = "" });
		_db.Insert(new Project { Key = ProjB, WorkspaceKey = OtherWs, Name = "B", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// The real catalog over the real core.db + a real on-disk DataDb dir — the tools are exercised
	// through exactly the door DI hands them at runtime.
	PetBox.Data.Contract.IDataDbCatalog Catalog => new DataDbCatalog(_db.Factory(), _dataDbs);

	// A key of `project` carrying `scopes`. The claim is what the tools authorize against.
	static IHttpContextAccessor Key(string project, string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", project), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) },
		};
	}

	// ── db_* : a DataDb of project B is invisible, unreadable and undeletable to a project-A key ──

	[Fact]
	public async Task DataDbTools_ProjectAKey_CannotReachProjectBsDataDbs()
	{
		var catalog = Catalog;
		// B owns a DataDb; A owns one of its own (so "returns nothing" cannot pass by accident).
		(await catalog.CreateAsync(ProjB, "secrets", null, null)).Should().BeOfType<PetBox.Data.Contract.DataDbChangeResult.Created>();
		(await catalog.CreateAsync(ProjA, "mine", null, null)).Should().BeOfType<PetBox.Data.Contract.DataDbChangeResult.Created>();

		var a = Key(ProjA, $"{ApiKeyScopes.DataRead},{ApiKeyScopes.DataSchema}");

		// Every db_* verb, addressed at B, refuses on the CLAIM — before the catalog is ever asked.
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DataDbTools.ListAsync(a, catalog, ProjB));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DataDbTools.DescribeAsync(a, catalog, ProjB, "secrets"));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DataDbTools.DeleteAsync(a, catalog, ProjB, "secrets"));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DataDbTools.CreateAsync(a, catalog, ProjB, "planted"));

		// B's catalog is untouched: nothing deleted, nothing planted.
		(await catalog.ListAsync(ProjB)).Select(d => d.Name).Should().Equal("secrets");

		// A's own project answers normally — and only with A's rows. The projectKey is an ADDRESS
		// (name is unique per project), so B's "secrets" is not even nameable from here.
		(await DataDbTools.ListAsync(a, catalog, ProjA)).Dbs.Select(d => d.Name).Should().Equal("mine");
		await Assert.ThrowsAsync<InvalidOperationException>(() => DataDbTools.DescribeAsync(a, catalog, ProjA, "secrets"));
	}

	// ── health_search : the project tag lives inside the report, and the service welds the filter in ──

	[Fact]
	public async Task HealthTools_ProjectAKey_CannotReadProjectBsReports()
	{
		PushHealth("api", ProjA);
		PushHealth("api", ProjB);

		var a = Key(ProjA, ApiKeyScopes.HealthRead);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			HealthTools.SearchAsync(a, _db.Factory().HealthReports(), ProjB));

		// Same service name in both projects — the answer for A must be A's report alone.
		var mine = await HealthTools.SearchAsync(a, _db.Factory().HealthReports(), ProjA);
		mine.Services.Should().ContainSingle();
		mine.Services[0].Tags["project"].Should().Be(ProjA);
	}

	// A cross-project ("*") key MAY name any project — but it still gets THAT project's rows only:
	// the wildcard widens the claim, never the filter.
	[Fact]
	public async Task HealthTools_WildcardKey_StillSeesOneProjectAtATime()
	{
		PushHealth("api", ProjA);
		PushHealth("worker", ProjB);

		var wild = Key(ProjectScope.AllProjects, ApiKeyScopes.HealthRead);

		(await HealthTools.SearchAsync(wild, _db.Factory().HealthReports(), ProjA))
			.Services.Select(s => s.Svc).Should().Equal("api");
		(await HealthTools.SearchAsync(wild, _db.Factory().HealthReports(), ProjB))
			.Services.Select(s => s.Svc).Should().Equal("worker");
	}

	// ── memory_* : the shared container of ANOTHER workspace is not reachable ──

	[Fact]
	public async Task MemoryTools_ProjectAKey_CannotReachAnotherWorkspacesSharedContainer()
	{
		var wsmem = _db.Factory().WorkspaceMemory();

		// The container of B's workspace, materialized (as B's own first write would).
		(await wsmem.EnsureAddressedContainerAsync(WorkspaceMemory.ContainerKeyFor(OtherWs))).Should().BeTrue();

		// A's key belongs to a project of ws-a: it may reach ws-a's container, and no other.
		(await wsmem.ReachableByAsync(WorkspaceMemory.ContainerKeyFor(OtherWs), ProjA)).Should().BeFalse(
			"a shared container is reachable only by keys of projects in ITS OWN workspace");
		(await wsmem.EnsureAddressedContainerAsync(WorkspaceMemory.ContainerKeyFor(Ws))).Should().BeTrue();
		(await wsmem.ReachableByAsync(WorkspaceMemory.ContainerKeyFor(Ws), ProjA)).Should().BeTrue();

		// …and a key with no project claim at all reaches nothing.
		(await wsmem.ReachableByAsync(WorkspaceMemory.ContainerKeyFor(Ws), null)).Should().BeFalse();

		// A container nobody's workspace owns is never conjured into existence by naming it.
		(await wsmem.EnsureAddressedContainerAsync("$ws-nosuch")).Should().BeFalse();
		_db.Projects.Any(p => p.Key == "$ws-nosuch").Should().BeFalse();
	}

	// ── scopes : the provisioning verbs still demand admin:provision ──

	[Fact]
	public async Task ProvisioningTools_WithoutAdminProvision_AreRefused()
	{
		// A perfectly good project key — with every scope EXCEPT the provisioning one.
		var weak = Key(ProjA, $"{ApiKeyScopes.DataRead},{ApiKeyScopes.DataSchema},{ApiKeyScopes.MemoryRead},{ApiKeyScopes.HealthRead}");
		var projects = new PetBox.Web.Auth.ProjectDirectory(_db.Factory());
		var keys = _db.Factory().AgentKeys();

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ProjectTools.CreateAsync(weak, projects, Ws, "sneaky"));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ProjectTools.ListAsync(weak, projects));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ApiKeyTools.CreateAsync(weak, keys, "k", ApiKeyScopes.DataRead, projectKey: ProjA));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ApiKeyTools.ListAsync(weak, keys, ProjA));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ApiKeyTools.UpdateAsync(weak, keys, "yb_key_whatever", name: "x"));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ApiKeyTools.DeleteAsync(weak, keys, "yb_key_whatever"));

		// Nothing was created by any of the refused calls. (The schema seeds its own bootstrap key, so
		// the assertion names OURS rather than counting the table.)
		_db.Projects.Count(p => p.Key == "sneaky").Should().Be(0);
		_db.ApiKeys.Count(k => k.Name == "k").Should().Be(0);
	}

	// admin:provision is FLEET-wide by design (it is what mints a cross-project key in the first
	// place), so it is not confined to the caller's own project — but it is the ONLY thing that
	// opens the provisioning surface, and the mint still obeys the catalog: an unknown project is
	// refused rather than minted against.
	[Fact]
	public async Task ProvisioningTools_WithAdminProvision_MintAgainstAnUnknownProject_IsRefused()
	{
		var admin = Key(ProjA, ApiKeyScopes.AdminProvision);
		var keys = _db.Factory().AgentKeys();

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			ApiKeyTools.CreateAsync(admin, keys, "k", ApiKeyScopes.DataRead, projectKey: "nosuchproject"));
		_db.ApiKeys.Count(k => k.Name == "k").Should().Be(0);

		// A project that exists mints normally, and the row lands on THAT project — not the caller's.
		var minted = await ApiKeyTools.CreateAsync(admin, keys, "k", ApiKeyScopes.DataRead, projectKey: ProjB);
		minted.ProjectKey.Should().Be(ProjB);
		_db.ApiKeys.Single(k => k.Name == "k").ProjectKey.Should().Be(ProjB);
	}

	void PushHealth(string svc, string project) =>
		_db.Insert(new HealthReport
		{
			Svc = svc,
			Name = svc,
			Tags = HealthTags.Canonical(new Dictionary<string, string>(StringComparer.Ordinal) { ["project"] = project }),
			Status = "ok",
			ReceivedAt = DateTime.UtcNow,
			Source = "push",
		});
}
