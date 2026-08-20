using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Web.Auth;
using PetBox.Web.Navigation;

namespace PetBox.Tests.Web;

// The sidebar's view of the catalog, built entirely from services (db-out-of-pages-into-services).
// These pin the ANSWERS that are load-bearing for tenancy: the tenant list a request gets depends on
// its ZONE (spec tenant-visibility-by-membership — /ui lists memberships, /ui/admin lists everything a
// sysadmin may reach), an account with no membership sees NOTHING (and must not fall back to $system —
// see workspace-access-isolation), and the $ws-* memory containers are not user projects and never
// appear in a project list.
//
// Because the zone is read from the REQUEST PATH, every test here states the path it is exercising;
// Nav() defaults to "/ui", the narrow zone, so a test that forgets fails closed rather than open.
public sealed class NavigationContextTests : IDisposable
{
	readonly List<string> _dirs = [];

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

	ICoreDbFactory NewDb()
	{
		var cs = TestSchema.NewTempConnectionString();
		_dirs.Add(Path.GetDirectoryName(new SqliteConnectionStringBuilder(cs).DataSource)!);
		TestSchema.Core(cs);
		return new CoreDbFactory(cs);
	}

	public void Dispose()
	{
		foreach (var dir in _dirs) TestDirs.CleanupOrDefer(dir);
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

	static Task SeedMember(ICoreDbFactory dbf, long userId, string ws, WorkspaceRole role = WorkspaceRole.Member) =>
		dbf.SeedMemberAsync(userId, ws, role);

	// `roles` mirrors what WorkspaceClaimsRefresher stamps on the identity each request; null means the
	// claim is ABSENT (a non-cookie identity), which must send the context to the database instead.
	static NavigationContext Nav(
		ICoreDbFactory dbf,
		long? userId = null,
		bool sysadmin = false,
		IEnumerable<(string WorkspaceKey, WorkspaceRole Role)>? roles = null,
		string? routeWorkspace = null,
		string? routeProject = null,
		bool authenticated = true,
		string path = "/ui")
	{
		var claims = new List<Claim>();
		if (userId is { } id) claims.Add(new Claim(PetBoxClaims.UserId, id.ToString()));
		if (sysadmin) claims.Add(new Claim(PetBoxClaims.IsSysAdmin, "true"));
		if (roles is not null)
			claims.Add(new Claim(PetBoxClaims.WorkspaceRoles, WorkspaceRoleAuthorizationHandler.SerializeRoles(roles)));

		var identity = authenticated ? new ClaimsIdentity(claims, "Cookies") : new ClaimsIdentity();
		var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
		// THE ZONE. NavigationContext reads the request path to decide which tenant list this request
		// is entitled to see, so every test now states the zone it is exercising. "/ui" is the user's
		// own zone (the default); "/ui/admin/..." is the administrative one.
		ctx.Request.Path = path;
		if (routeWorkspace is not null) ctx.Request.RouteValues["workspaceKey"] = routeWorkspace;
		if (routeProject is not null) ctx.Request.RouteValues["projectKey"] = routeProject;

		return new NavigationContext(
			new FakeAccessor(ctx),
			new ProjectDirectory(dbf),
			new WorkspaceAdminService(dbf, new ProjectDirectory(dbf), new WorkspaceMembershipService(dbf), new WorkspaceProvisioning(dbf, new WorkspaceMembershipService(dbf))),
			new WorkspaceMembershipService(dbf),
			Features());
	}

	// ── spec tenant-visibility-by-membership ────────────────────────────────────────────────────
	// The five below are the card's five acceptance criteria, one test each. They are a MATCHED SET:
	// criteria 1 and 4 narrow the user zone, criteria 2 and 3 prove the narrowing did not cost the
	// operator anything, and 5 proves an ordinary member noticed nothing. Deleting the isSysAdmin arm
	// outright would pass 1, 4 and 5 while silently breaking 2 and 3 — which is why they are here.

	// (1) User zone: a sysadmin who is not a member of `beta` does not see `beta` — not in the
	// workspace list the selector renders, and not as a key of the project tree the project selector
	// and /ui/search are both sliced from.
	[Fact]
	public async Task Criterion1_user_zone_hides_a_tenant_the_sysadmin_is_not_a_member_of()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "secret", "beta");
		var uid = SeedUser(dbf, "root");
		await SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, sysadmin: true, roles: [("alpha", WorkspaceRole.Admin)], path: "/ui");

		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal(
			["alpha"],
			"the personal zone lists tenants by MEMBERSHIP — the system permission is not a listing gate");
		nav.ProjectsByWorkspace.Keys.Should().NotContain("beta",
			"the project tree is keyed off the same list, so another tenant's projects cannot leak transitively");
		nav.ProjectsByWorkspace.Keys.Should().Equal(["alpha"]);
	}

	// (2) THE OTHER HALF, and the one that would make this change a regression if it were missing:
	// the SAME principal in the admin zone still enumerates every tenant. Both selectors are shared
	// between the zones, so if this list narrowed too, the operator could no longer pick another
	// tenant out of the admin sidebar at all.
	[Fact]
	public async Task Criterion2_admin_zone_still_enumerates_every_tenant_for_a_sysadmin()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		var uid = SeedUser(dbf, "root");
		await SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, sysadmin: true, roles: [("alpha", WorkspaceRole.Admin)],
			path: "/ui/admin/sys/workspaces");

		// $system is seeded by the migrations themselves; the list stays ordered by key.
		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal(
			["$system", "alpha", "beta"],
			"administering every tenant is the admin zone's SUBJECT — this list is not a leak");
		nav.AvailableWorkspaces.Select(w => w.Name).Should().Contain(["ALPHA", "BETA"],
			"the admin selector renders names");
	}

	// (3) The right is untouched: an ADDRESSED /ui/{W}/... URL still resolves W for a sysadmin who is
	// not a member of it. Visibility changed, access did not — so ResolveWorkspace must consult
	// reachability, never the (narrower) list the zone renders.
	[Fact]
	public async Task Criterion3_addressed_url_to_a_foreign_tenant_still_resolves_in_the_user_zone()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "secret", "beta");
		var uid = SeedUser(dbf, "root");
		await SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, sysadmin: true, roles: [("alpha", WorkspaceRole.Admin)],
			routeWorkspace: "beta", path: "/ui/beta/dashboard");

		nav.CurrentWorkspaceKey.Should().Be("beta",
			"absence from the sidebar is NOT a denial — the addressed tenant still resolves");
		nav.HasWorkspace.Should().BeTrue();
		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal(["alpha"],
			"...and reaching beta by URL still does not add it to the personal zone's list");

		// The same must hold when the workspace is implied by a PROJECT route: that path misses the
		// membership-built tree and falls through to the cold directory lookup, which authorizes on
		// reach. This is the arm a naive "filter everything" fix silently breaks.
		var byProject = Nav(dbf, uid, sysadmin: true, roles: [("alpha", WorkspaceRole.Admin)],
			routeProject: "secret", path: "/ui/beta/secret/tasks");
		byProject.CurrentWorkspaceKey.Should().Be("beta");
	}

	// (4) The second user-zone surface. /ui/search fans out over ProjectsByWorkspace
	// (CrossScopeTaskSearchService), so the guard has to hold on a bare /ui/search request — one that
	// carries no route workspace at all to fall back on.
	[Fact]
	public async Task Criterion4_cross_scope_search_fans_out_over_memberships_only()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "secret", "beta");
		var uid = SeedUser(dbf, "root");
		await SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, sysadmin: true, roles: [("alpha", WorkspaceRole.Admin)], path: "/ui/search");

		// This dictionary IS the fan-out's job list (CrossScopeTaskSearchService.SearchAsync passes
		// nav.ProjectsByWorkspace straight through), so a project absent here is a project the search
		// never opens a DI scope for.
		nav.ProjectsByWorkspace.SelectMany(kv => kv.Value).Select(p => p.Key)
			.Should().Equal(["app"], "a foreign tenant's projects are not in the search fan-out");
		nav.ProjectsByWorkspace.Keys.Should().NotContain("beta");
	}

	// (5) The regression guard for everyone who is NOT a sysadmin: an ordinary member of beta sees
	// exactly what they saw before, in BOTH zones.
	[Fact]
	public async Task Criterion5_an_ordinary_member_sees_no_change_in_either_zone()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "secret", "beta");
		var uid = SeedUser(dbf, "eve");
		await SeedMember(dbf, uid, "beta");

		var userZone = Nav(dbf, uid, roles: [("beta", WorkspaceRole.Member)], path: "/ui");
		userZone.AvailableWorkspaces.Select(w => w.Key).Should().Equal(["beta"]);
		userZone.ProjectsByWorkspace["beta"].Select(p => p.Key).Should().Equal(["secret"]);

		// No system permission → the admin zone shows them the same one tenant. The zone split hands
		// out the wide catalog on the STRENGTH of the permission, never on the strength of the path.
		var adminZone = Nav(dbf, uid, roles: [("beta", WorkspaceRole.Member)], path: "/ui/admin/ws/beta");
		adminZone.AvailableWorkspaces.Select(w => w.Key).Should().Equal(["beta"],
			"a non-sysadmin gains nothing by being on an /ui/admin path");
	}

	// The legacy free pass, retired. The second arm of the old condition handed the WHOLE catalog to
	// any authenticated principal carrying no yb:user_id, on the theory that a "legacy admin with no
	// User row" would otherwise get an empty sidebar. No cookie session can be in that state
	// (CredentialAuthenticator reads db.Users and rejects a miss, and Login always stamps the claim),
	// so the only principal that ever reached it was an api-key one rendering a /ui page — and it saw
	// every tenant in the installation. It now sees none.
	[Fact]
	public void A_principal_with_no_user_id_claim_gets_nothing_instead_of_the_whole_catalog()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");

		var nav = Nav(dbf, userId: null, roles: null, path: "/ui");

		nav.IsAuthenticated.Should().BeTrue("the principal is authenticated, just not a known user");
		nav.AvailableWorkspaces.Should().BeEmpty(
			"no identity to filter by means no memberships — not a free pass to every tenant");
		nav.HasWorkspace.Should().BeFalse();
	}

	// The bootstrap admin — the account the owner signs in with — is NOT blinded by any of the above,
	// and that is a fact about AdminBootstrapper, not a hope: it writes the Users row and the $system
	// Admin membership as ONE transaction. So the identity that carries yb:sysadmin also carries a
	// real membership, and its personal zone lists $system like any other member's.
	[Fact]
	public async Task The_bootstrap_admin_keeps_a_populated_user_zone_through_its_seeded_membership()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "beta");
		var uid = SeedUser(dbf, "admin");
		// Exactly the pair AdminBootstrapper.EnsureAdminUser commits on first boot.
		await SeedMember(dbf, uid, "$system", WorkspaceRole.Admin);

		var nav = Nav(dbf, uid, sysadmin: true, roles: [("$system", WorkspaceRole.Admin)], path: "/ui");

		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal(["$system"],
			"the seeded $system membership keeps the owner's own zone populated after the free pass is gone");
		nav.HasWorkspace.Should().BeTrue();
		nav.CurrentWorkspaceKey.Should().Be("$system");
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
	public async Task A_member_sees_only_their_own_workspaces()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "secret", "beta");
		var uid = SeedUser(dbf, "eve");
		await SeedMember(dbf, uid, "alpha");

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
	public async Task Membership_falls_back_to_the_database_when_the_claim_is_absent()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		var uid = SeedUser(dbf, "eve");
		await SeedMember(dbf, uid, "beta");

		var nav = Nav(dbf, uid, roles: null);

		nav.AvailableWorkspaces.Select(w => w.Key).Should().Equal("beta");
	}

	[Fact]
	public async Task Workspace_memory_containers_are_not_projects()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "$ws-alpha", "alpha");
		var uid = SeedUser(dbf, "eve");
		await SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, roles: [("alpha", WorkspaceRole.Member)], routeWorkspace: "alpha");

		nav.ProjectsInCurrentWorkspace.Select(p => p.Key).Should().Equal(["app"],
			"the $ws-* container has no logs/dbs/boards — it is not a project tree entry");
		nav.ProjectsByWorkspace["alpha"].Select(p => p.Key).Should().Equal(["app"]);
	}

	// The container HAS routes (/ui/{ws}/$ws-{ws}/memory) even though it is not in the tree, so the
	// workspace must still resolve from it — the cold path that asks the directory by key.
	[Fact]
	public async Task A_container_route_still_resolves_its_workspace()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "$ws-alpha", "alpha");
		var uid = SeedUser(dbf, "eve");
		await SeedMember(dbf, uid, "alpha");

		var nav = Nav(dbf, uid, roles: [("alpha", WorkspaceRole.Member)], routeProject: "$ws-alpha");

		nav.CurrentWorkspaceKey.Should().Be("alpha");
		nav.CurrentProjectKey.Should().Be("$ws-alpha", "the route names the project, tree or no tree");
	}

	[Fact]
	public async Task A_project_route_resolves_the_workspace_without_a_route_workspace()
	{
		var dbf = NewDb();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "beta");
		var uid = SeedUser(dbf, "eve");
		await SeedMember(dbf, uid, "alpha");
		await SeedMember(dbf, uid, "beta");

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
