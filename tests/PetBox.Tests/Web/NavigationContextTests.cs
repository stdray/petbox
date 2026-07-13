using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Web.Auth;
using PetBox.Web.Navigation;

namespace PetBox.Tests.Web;

// The sidebar's view of the catalog, now built entirely from services (db-out-of-pages-into-services).
// These pin the ANSWERS across that move — the three that are load-bearing for tenancy: a sysadmin sees
// every workspace, an account with no membership sees NOTHING (and must not fall back to $system — see
// workspace-access-isolation), and the $ws-* memory containers are not user projects and never appear
// in a project list.
public sealed class NavigationContextTests
{
	sealed class FakeAccessor(HttpContext ctx) : IHttpContextAccessor
	{
		public HttpContext? HttpContext { get; set; } = ctx;
	}

	static FeatureFlags Features()
	{
		var cfg = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Features:Tasks"] = "true",
				["Features:Memory"] = "true",
				["Features:Data"] = "true",
			})
			.Build();
		return new FeatureFlags(cfg);
	}

	static ICoreDbFactory NewDb()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		return new CoreDbFactory(cs);
	}

	static void SeedWorkspace(ICoreDbFactory dbf, string key)
	{
		using var db = dbf.Open();
		db.Insert(new Workspace { Key = key, Name = key.ToUpperInvariant(), Description = "", CreatedAt = DateTime.UtcNow });
	}

	static void SeedProject(ICoreDbFactory dbf, string key, string ws)
	{
		using var db = dbf.Open();
		db.Insert(new Project { Key = key, WorkspaceKey = ws, Name = key, Description = "" });
	}

	static long SeedUser(ICoreDbFactory dbf, string name)
	{
		using var db = dbf.Open();
		return db.InsertWithInt64Identity(new User { Username = name, PasswordHash = "x", CreatedAt = DateTime.UtcNow });
	}

	static void SeedMember(ICoreDbFactory dbf, long userId, string ws, WorkspaceRole role = WorkspaceRole.Member)
	{
		using var db = dbf.Open();
		db.Insert(new WorkspaceMember { UserId = userId, WorkspaceKey = ws, Role = role });
	}

	// `roles` mirrors what WorkspaceClaimsRefresher stamps on the identity each request; null means the
	// claim is ABSENT (a non-cookie identity), which must send the context to the database instead.
	static NavigationContext Nav(
		ICoreDbFactory dbf,
		long? userId = null,
		bool sysadmin = false,
		IEnumerable<(string WorkspaceKey, WorkspaceRole Role)>? roles = null,
		string? routeWorkspace = null,
		string? routeProject = null,
		bool authenticated = true)
	{
		var claims = new List<Claim>();
		if (userId is { } id) claims.Add(new Claim(PetBoxClaims.UserId, id.ToString()));
		if (sysadmin) claims.Add(new Claim(PetBoxClaims.IsSysAdmin, "true"));
		if (roles is not null)
			claims.Add(new Claim(PetBoxClaims.WorkspaceRoles, WorkspaceRoleAuthorizationHandler.SerializeRoles(roles)));

		var identity = authenticated ? new ClaimsIdentity(claims, "Cookies") : new ClaimsIdentity();
		var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
		if (routeWorkspace is not null) ctx.Request.RouteValues["workspaceKey"] = routeWorkspace;
		if (routeProject is not null) ctx.Request.RouteValues["projectKey"] = routeProject;

		return new NavigationContext(
			new FakeAccessor(ctx),
			new ProjectDirectory(dbf),
			new WorkspaceAdminService(dbf, new ProjectDirectory(dbf), new WorkspaceMembershipService(dbf), new WorkspaceProvisioning(dbf)),
			new WorkspaceMembershipService(dbf),
			Features());
	}

	[Fact]
	public void Sysadmin_sees_every_workspace_regardless_of_membership()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		var uid = SeedUser(dbf, "root");

		var nav = Nav(dbf, uid, sysadmin: true, roles: []);

		// $system is seeded by the migrations themselves — a sysadmin sees it too, and the list stays
		// ordered by key.
		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal(
			["$system", "alpha", "beta"],
			"a sysadmin admins every workspace — membership is not the gate");
		nav.AvailableWorkspaces.Select(w => w.Name).Should().Contain(["ALPHA", "BETA"], "the selector renders names");
		nav.HasWorkspace.Should().BeTrue();
	}

	[Fact]
	public void An_account_with_no_membership_gets_an_empty_list_and_no_workspace()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "app", "alpha");
		var uid = SeedUser(dbf, "nomad");

		// No memberships → the refresher stamps an EMPTY yb:ws_roles, which is indistinguishable from
		// "no claim", so this also exercises the database fallback in MembershipKeys.
		var nav = Nav(dbf, uid, roles: []);

		nav.AvailableWorkspaces.Should().BeEmpty("a fresh account belongs to nothing");
		nav.HasWorkspace.Should().BeFalse(
			"it must NOT fall back to $system — that handed a non-member someone else's dashboard");
		nav.CurrentWorkspaceKey.Should().BeNull();
		nav.CurrentProjectKey.Should().BeNull();
		nav.ProjectsInCurrentWorkspace.Should().BeEmpty();
		nav.ProjectsByWorkspace.Should().BeEmpty();
	}

	[Fact]
	public void A_member_sees_only_their_own_workspaces()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "secret", "beta");
		var uid = SeedUser(dbf, "eve");
		SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, roles: [("alpha", WorkspaceRole.Member)], routeWorkspace: "alpha");

		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal(["alpha"]);
		nav.ProjectsByWorkspace.Keys.Should().Equal(["alpha"], "another tenant's tree is not in the dictionary");
		nav.ProjectsInCurrentWorkspace.Select(p => p.Key).Should().Equal(["app"]);
		nav.CurrentWorkspaceKey.Should().Be("alpha");
		nav.CurrentProjectKey.Should().Be("app");
	}

	// The membership keys are read from yb:ws_roles, whose "ws=Role,ws=Role" format is owned by
	// WorkspaceRoleAuthorizationHandler.SerializeRoles. This round-trips a claim built by THAT
	// serializer, so the two cannot drift apart in silence.
	[Fact]
	public void Membership_is_read_from_the_claim_the_refresher_writes()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedWorkspace(dbf, "gamma");
		var uid = SeedUser(dbf, "eve");
		// NOTE: no WorkspaceMember rows at all — the claim is the ONLY source here, so if it were
		// ignored the list would come back empty.
		var nav = Nav(dbf, uid, roles: [("alpha", WorkspaceRole.Admin), ("gamma", WorkspaceRole.Viewer)]);

		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal("alpha", "gamma");
	}

	// ...and when the claim is absent (an identity the cookie refresher never touched), the database is
	// still the answer — the claim is an optimisation, never the only source of truth.
	[Fact]
	public void Membership_falls_back_to_the_database_when_the_claim_is_absent()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		var uid = SeedUser(dbf, "eve");
		SeedMember(dbf, uid, "beta");

		var nav = Nav(dbf, uid, roles: null);

		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal("beta");
	}

	[Fact]
	public void Workspace_memory_containers_are_not_projects()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "$ws-alpha", "alpha");
		var uid = SeedUser(dbf, "eve");
		SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, roles: [("alpha", WorkspaceRole.Member)], routeWorkspace: "alpha");

		nav.ProjectsInCurrentWorkspace.Select(p => p.Key).Should().Equal(["app"],
			"the $ws-* container has no logs/dbs/boards — it is not a project tree entry");
		nav.ProjectsByWorkspace["alpha"].Select(p => p.Key).Should().Equal(["app"]);
	}

	// The container HAS routes (/ui/{ws}/$ws-{ws}/memory) even though it is not in the tree, so the
	// workspace must still resolve from it — the cold path that asks the directory by key.
	[Fact]
	public void A_container_route_still_resolves_its_workspace()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "$ws-alpha", "alpha");
		var uid = SeedUser(dbf, "eve");
		SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, roles: [("alpha", WorkspaceRole.Member)], routeProject: "$ws-alpha");

		nav.CurrentWorkspaceKey.Should().Be("alpha");
		nav.CurrentProjectKey.Should().Be("$ws-alpha", "the route names the project, tree or no tree");
	}

	[Fact]
	public void A_project_route_resolves_the_workspace_without_a_route_workspace()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "beta");
		var uid = SeedUser(dbf, "eve");
		SeedMember(dbf, uid, "alpha");
		SeedMember(dbf, uid, "beta");

		var nav = Nav(
			dbf, uid,
			roles: [("alpha", WorkspaceRole.Member), ("beta", WorkspaceRole.Member)],
			routeProject: "app");

		nav.CurrentWorkspaceKey.Should().Be("beta", "the project's own workspace wins over the first membership");
	}

	[Fact]
	public void An_anonymous_request_has_no_workspaces_and_touches_nothing()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "app", "alpha");

		var nav = Nav(dbf, authenticated: false);

		nav.IsAuthenticated.Should().BeFalse();
		nav.AvailableWorkspaces.Should().BeEmpty();
		nav.HasWorkspace.Should().BeFalse();
		nav.ProjectsInCurrentWorkspace.Should().BeEmpty();
	}
}
