using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Auth;

// The ONE tenant access decision (spec authz-tenant-default-deny): (principal, TenantRef) ->
// TenantAccess, for both kinds of target. What is asserted here is the OUTCOME (allowed vs not) —
// the denial reason is a diagnostic and is asserted only where it is the point of the case
// (containment vs "wrong tenant").
//
// Fixture shape: workspace `wsa` owns `proja` and `sandboxproj` (the latter flagged sandbox);
// workspace `wsb` owns `projb`. `orphan` is a project row with no workspace ("" — the model's
// default, not null); `ghost` does not exist at all.
public sealed class TenantAuthorizerTests
{
	const string WsA = "wsa";
	const string WsB = "wsb";
	const string ProjA = "proja";
	const string ProjB = "projb";
	const string SandboxProj = "sandboxproj";

	static readonly FakeCatalog Catalog = new(
		workspaceOf: new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[ProjA] = WsA,
			[SandboxProj] = WsA,
			[ProjB] = WsB,
			["orphan"] = "",
		},
		sandboxProjects: new HashSet<string>(StringComparer.Ordinal) { SandboxProj });

	static readonly TenantAuthorizer Authorizer = new(Catalog);

	// --- api key, PROJECT target: the existing primitive, reached through the new door -------------

	[Theory]
	[InlineData(ProjA, ProjA, TenantAccess.Allowed)]           // own project
	[InlineData(ProjA, ProjB, TenantAccess.NotAuthorized)]     // another tenant's project
	[InlineData("*", ProjA, TenantAccess.Allowed)]             // cross-project claim: any project
	[InlineData("*", "ghost", TenantAccess.Allowed)]           // unchanged: identity, not existence
	[InlineData("", ProjA, TenantAccess.NotAuthorized)]        // no claim value -> nothing
	public async Task ApiKey_ProjectTarget(string claim, string projectKey, TenantAccess expected) =>
		(await Authorizer.AuthorizeAsync(ApiKey(claim), TenantRef.Project(projectKey)))
			.Should().Be(expected);

	// --- api key, WORKSPACE target: "a project claim authorizes ITS workspace", once ---------------

	[Theory]
	[InlineData(ProjA, WsA, TenantAccess.Allowed)]             // claim's own workspace
	[InlineData(ProjA, WsB, TenantAccess.NotAuthorized)]       // a workspace it does not belong to
	[InlineData("*", WsA, TenantAccess.Allowed)]               // cross-project claim: any workspace
	[InlineData("*", WsB, TenantAccess.Allowed)]
	[InlineData("ghost", WsA, TenantAccess.NotAuthorized)]     // claim names no real project
	[InlineData("orphan", "", TenantAccess.NoTenant)]          // blank target, before anything else
	[InlineData("", WsA, TenantAccess.NotAuthorized)]
	public async Task ApiKey_WorkspaceTarget(string claim, string workspaceKey, TenantAccess expected) =>
		(await Authorizer.AuthorizeAsync(ApiKey(claim), TenantRef.Workspace(workspaceKey)))
			.Should().Be(expected);

	// A project row whose WorkspaceKey is "" (the model default) must not match a target that got
	// here as a resolved tenant — and cannot be used to reach the empty-string workspace either.
	[Fact]
	public async Task ApiKey_ClaimProjectWithBlankWorkspace_AuthorizesNoWorkspace()
	{
		(await Authorizer.AuthorizeAsync(ApiKey("orphan"), TenantRef.Workspace(WsA)))
			.Should().Be(TenantAccess.NotAuthorized);
		(await Authorizer.AuthorizeAsync(ApiKey("orphan"), TenantRef.Workspace("  ")))
			.Should().Be(TenantAccess.NoTenant);
	}

	// --- sandbox containment: orthogonal to the claim, and the wildcard does not buy past it -------

	[Theory]
	[InlineData(SandboxProj, TenantAccess.Allowed)]
	[InlineData(ProjA, TenantAccess.SandboxContainment)]
	public async Task SandboxOnlyKey_ProjectTarget(string projectKey, TenantAccess expected) =>
		(await Authorizer.AuthorizeAsync(ApiKey("*", sandboxOnly: true), TenantRef.Project(projectKey)))
			.Should().Be(expected);

	// A project-scoped claim that doesn't match is a plain denial, NOT containment — identity is
	// checked first, exactly as ProjectScope short-circuits.
	[Fact]
	public async Task SandboxOnlyKey_WrongProject_DeniesOnIdentityNotContainment() =>
		(await Authorizer.AuthorizeAsync(ApiKey(SandboxProj, sandboxOnly: true), TenantRef.Project(ProjB)))
			.Should().Be(TenantAccess.NotAuthorized);

	// CONTAINMENT DOES NOT REACH A WORKSPACE TARGET — the owner's decision, reversing what this test
	// used to pin (SandboxContainment for every workspace target).
	//
	// The old reading was defensible on its own terms: a workspace carries no Sandbox flag and is
	// broader than any project inside it, so containment "cannot be satisfied". But it was STRICTER
	// than anything the system actually does — no workspace-target check in the tree looks at the flag
	// (ConfigApi.AuthorizeWorkspaceAsync does not; MemoryTools' AssertMemoryProjectAsync does not) — so
	// letting the PEP enforce it would have REFUSED keys that work today, most visibly a sandboxOnly
	// key curating its own workspace's shared memory. Acceptance criterion 1 of work
	// `authz-default-deny-delivery` ("ключ, работавший до перехода, работает после") outranks being
	// stricter, so the pre-existing outcome is preserved on purpose.
	//
	// The PROJECT axis is untouched and still contains — see the two cases above. Tightening the
	// workspace axis is a separate decision with its own blast radius (shared memory, config, every
	// workspace page), not a side effect of wiring up enforcement.
	[Theory]
	[InlineData("*")]
	[InlineData(SandboxProj)]
	public async Task SandboxOnlyKey_WorkspaceTarget_IsNotContained(string claim) =>
		(await Authorizer.AuthorizeAsync(ApiKey(claim, sandboxOnly: true), TenantRef.Workspace(WsA)))
			.Should().Be(TenantAccess.Allowed);

	// --- the unresolved target: a denial for everyone, wildcard included --------------------------

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData(null)]
	public async Task BlankTarget_IsDenied_EvenForTheCrossProjectClaim(string? key)
	{
		(await Authorizer.AuthorizeAsync(ApiKey("*"), TenantRef.Project(key))).Should().Be(TenantAccess.NoTenant);
		(await Authorizer.AuthorizeAsync(ApiKey("*"), TenantRef.Workspace(key))).Should().Be(TenantAccess.NoTenant);
		(await Authorizer.AuthorizeAsync(SysAdmin(), TenantRef.Project(key))).Should().Be(TenantAccess.NoTenant);
	}

	// The ZERO VALUE of the reference type is already the deny case: a surface that forgets to name a
	// tenant does not get a default one.
	[Fact]
	public async Task DefaultTenantRef_IsUnresolved_AndDenied()
	{
		default(TenantRef).IsResolved.Should().BeFalse();
		default(TenantRef).Key.Should().BeEmpty();
		(await Authorizer.AuthorizeAsync(ApiKey("*"), default)).Should().Be(TenantAccess.NoTenant);
	}

	// --- no principal / nothing authenticated -----------------------------------------------------

	[Fact]
	public async Task NoPrincipal_IsDenied()
	{
		(await Authorizer.AuthorizeAsync(null, TenantRef.Project(ProjA))).Should().Be(TenantAccess.NotAuthorized);
		(await Authorizer.AuthorizeAsync(new ClaimsPrincipal(), TenantRef.Project(ProjA))).Should().Be(TenantAccess.NotAuthorized);
	}

	// An UNAUTHENTICATED identity carrying a perfectly good project claim authorizes nothing: the
	// claim only means something because a scheme vouched for it.
	[Fact]
	public async Task UnauthenticatedIdentityWithAProjectClaim_IsDenied()
	{
		var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ApiKeyAuthenticationHandler.ProjectClaim, "*")]));
		(await Authorizer.AuthorizeAsync(principal, TenantRef.Project(ProjA))).Should().Be(TenantAccess.NotAuthorized);
	}

	// --- cookie principal: the same boundary, membership as the claim ------------------------------

	[Fact]
	public async Task User_WorkspaceTarget_MemberIsAllowed_NonMemberIsNot()
	{
		var user = User((WsA, WorkspaceRole.Viewer));
		(await Authorizer.AuthorizeAsync(user, TenantRef.Workspace(WsA))).Should().Be(TenantAccess.Allowed);
		(await Authorizer.AuthorizeAsync(user, TenantRef.Workspace(WsB))).Should().Be(TenantAccess.NotAuthorized);
	}

	// The project target resolves its workspace from the CATALOG, never from a URL — which is what
	// makes a wsA-shaped URL pointed at a wsB project answer on wsB's membership.
	[Fact]
	public async Task User_ProjectTarget_AnswersOnTheProjectsRealWorkspace()
	{
		var user = User((WsA, WorkspaceRole.Admin));
		(await Authorizer.AuthorizeAsync(user, TenantRef.Project(ProjA))).Should().Be(TenantAccess.Allowed);
		(await Authorizer.AuthorizeAsync(user, TenantRef.Project(ProjB))).Should().Be(TenantAccess.NotAuthorized);
	}

	// An unknown project is a denial, and it is the SAME denial as a wrong-tenant one — the decision
	// point must not become an existence oracle.
	[Fact]
	public async Task User_UnknownOrOrphanProject_IsDenied_WithNoDistinctReason()
	{
		var user = User((WsA, WorkspaceRole.Admin));
		(await Authorizer.AuthorizeAsync(user, TenantRef.Project("ghost"))).Should().Be(TenantAccess.NotAuthorized);
		(await Authorizer.AuthorizeAsync(user, TenantRef.Project("orphan"))).Should().Be(TenantAccess.NotAuthorized);
	}

	// A user with no membership claim at all reaches nothing. (Absence of the claim is "no tenant
	// granted here", which is the default-deny answer, not a fallback to something wider.)
	[Fact]
	public async Task User_WithoutMembershipClaims_IsDenied()
	{
		var user = User();
		(await Authorizer.AuthorizeAsync(user, TenantRef.Workspace(WsA))).Should().Be(TenantAccess.NotAuthorized);
		(await Authorizer.AuthorizeAsync(user, TenantRef.Project(ProjA))).Should().Be(TenantAccess.NotAuthorized);
	}

	// Sysadmin reaches every tenant — but still not a nonexistent one, and still not an unnamed one.
	[Fact]
	public async Task SysAdmin_ReachesEveryRealTenant_ButNotAGhost()
	{
		(await Authorizer.AuthorizeAsync(SysAdmin(), TenantRef.Workspace(WsB))).Should().Be(TenantAccess.Allowed);
		(await Authorizer.AuthorizeAsync(SysAdmin(), TenantRef.Project(ProjB))).Should().Be(TenantAccess.Allowed);
		(await Authorizer.AuthorizeAsync(SysAdmin(), TenantRef.Project("ghost"))).Should().Be(TenantAccess.NotAuthorized);
	}

	// Any role is enough for the TENANT question — which role a surface demands is the other axis and
	// is not asked here.
	[Theory]
	[InlineData(WorkspaceRole.Admin)]
	[InlineData(WorkspaceRole.Member)]
	[InlineData(WorkspaceRole.Viewer)]
	public async Task User_AnyRole_SatisfiesTheTenantQuestion(WorkspaceRole role) =>
		(await Authorizer.AuthorizeAsync(User((WsA, role)), TenantRef.Workspace(WsA)))
			.Should().Be(TenantAccess.Allowed);

	// --- both credentials at once ------------------------------------------------------------------

	// A caller presenting BOTH an api key and a session cookie is answered on the KEY: whichever
	// identity happens to be first in the merged principal must not change the decision, and the
	// answer is never broader than either credential alone would give.
	[Fact]
	public async Task MergedPrincipal_IsAnsweredOnTheApiKeyIdentity_RegardlessOfOrder()
	{
		var keyIdentity = KeyIdentity(ProjA, sandboxOnly: false);
		var cookieIdentity = UserIdentity(sysAdmin: false, (WsB, WorkspaceRole.Admin));

		foreach (var principal in new[]
		{
			new ClaimsPrincipal([keyIdentity, cookieIdentity]),
			new ClaimsPrincipal([cookieIdentity, keyIdentity]),
		})
		{
			// The cookie alone would reach wsB; the key does not, so the merged caller does not.
			(await Authorizer.AuthorizeAsync(principal, TenantRef.Workspace(WsB))).Should().Be(TenantAccess.NotAuthorized);
			(await Authorizer.AuthorizeAsync(principal, TenantRef.Workspace(WsA))).Should().Be(TenantAccess.Allowed);
		}
	}

	// --- the three hand-written call sites, by outcome ---------------------------------------------

	// ConfigApi.AuthorizeWorkspaceAsync, case for case: empty claim denies, wildcard passes, a
	// project claim passes only for its own workspace, an unknown claim project denies.
	[Theory]
	[InlineData("", WsA, false)]
	[InlineData("*", WsA, true)]
	[InlineData("*", WsB, true)]
	[InlineData(ProjA, WsA, true)]
	[InlineData(ProjA, WsB, false)]
	[InlineData("ghost", WsA, false)]
	public async Task MatchesConfigApiAuthorizeWorkspace(string claim, string workspaceKey, bool allowed) =>
		(await Authorizer.AuthorizeAsync(ApiKey(claim), TenantRef.Workspace(workspaceKey)) == TenantAccess.Allowed)
			.Should().Be(allowed);

	// WorkspaceRoleAuthorizationHandler, tenant half: sysadmin passes everywhere, a member passes in
	// its own workspace, a stranger does not. (Its MinRole half is the role axis and is not covered
	// here — see the card: that check must survive this decision point, not be replaced by it.)
	[Fact]
	public async Task MatchesWorkspaceRoleHandler_TenantHalf()
	{
		(await Authorizer.AuthorizeAsync(SysAdmin(), TenantRef.Workspace(WsA))).Should().Be(TenantAccess.Allowed);
		(await Authorizer.AuthorizeAsync(User((WsA, WorkspaceRole.Member)), TenantRef.Workspace(WsA))).Should().Be(TenantAccess.Allowed);
		(await Authorizer.AuthorizeAsync(User((WsA, WorkspaceRole.Member)), TenantRef.Workspace(WsB))).Should().Be(TenantAccess.NotAuthorized);
	}

	// ProjectWorkspaceBindingFilter's class of attack — a wsA URL pointed at a wsB project — is
	// answered without consulting the URL at all: the tenant is the PROJECT, and its workspace comes
	// from the catalog. A member of only wsA is refused, exactly as the filter refuses.
	[Fact]
	public async Task MatchesProjectWorkspaceBindingFilter_CrossTenantUrl()
	{
		var wsAOnly = User((WsA, WorkspaceRole.Admin));
		(await Authorizer.AuthorizeAsync(wsAOnly, TenantRef.Project(ProjB))).Should().Be(TenantAccess.NotAuthorized);

		// DIFFERENCE, deliberate: a caller who belongs to BOTH workspaces is allowed here, where the
		// filter 404s purely because the route's {workspaceKey} disagrees with the project's. The
		// decision point answers "may this principal touch this tenant", and the answer is yes; a URL
		// that names a workspace the project isn't in is a routing defect, not an access one.
		var both = User((WsA, WorkspaceRole.Admin), (WsB, WorkspaceRole.Viewer));
		(await Authorizer.AuthorizeAsync(both, TenantRef.Project(ProjB))).Should().Be(TenantAccess.Allowed);
	}

	// --- helpers -----------------------------------------------------------------------------------

	static ClaimsPrincipal ApiKey(string? claim, bool sandboxOnly = false) =>
		new(KeyIdentity(claim, sandboxOnly));

	static ClaimsIdentity KeyIdentity(string? claim, bool sandboxOnly)
	{
		var claims = new List<Claim>();
		if (claim is not null) claims.Add(new Claim(ApiKeyAuthenticationHandler.ProjectClaim, claim));
		if (sandboxOnly) claims.Add(new Claim(ApiKeyAuthenticationHandler.SandboxOnlyClaim, "true"));
		return new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName);
	}

	static ClaimsPrincipal User(params (string WorkspaceKey, WorkspaceRole Role)[] roles) =>
		new(UserIdentity(sysAdmin: false, roles));

	static ClaimsPrincipal SysAdmin() => new(UserIdentity(sysAdmin: true));

	static ClaimsIdentity UserIdentity(bool sysAdmin, params (string WorkspaceKey, WorkspaceRole Role)[] roles)
	{
		var claims = new List<Claim> { new(PetBoxClaims.UserId, "1") };
		if (sysAdmin) claims.Add(new Claim(PetBoxClaims.IsSysAdmin, "true"));
		if (roles.Length > 0)
			claims.Add(new Claim(PetBoxClaims.WorkspaceRoles, WorkspaceRoleAuthorizationHandler.SerializeRoles(roles)));
		return new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
	}

	sealed class FakeCatalog(IReadOnlyDictionary<string, string> workspaceOf, HashSet<string> sandboxProjects) : IProjectCatalog
	{
		public Task<IReadOnlyList<string>> ListProjectKeysAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<IReadOnlyList<string>> ListWorkspaceKeysAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<IReadOnlyList<string>> ListMemoryProjectKeysAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<IReadOnlyList<string>> ListTaskProjectKeysAsync(CancellationToken ct = default) =>
			throw new NotSupportedException();

		public Task<bool> IsSandboxAsync(string projectKey, CancellationToken ct = default) =>
			Task.FromResult(sandboxProjects.Contains(projectKey));

		public Task<string?> WorkspaceKeyOfAsync(string projectKey, CancellationToken ct = default) =>
			Task.FromResult(workspaceOf.TryGetValue(projectKey, out var ws) ? ws : null);
	}
}
