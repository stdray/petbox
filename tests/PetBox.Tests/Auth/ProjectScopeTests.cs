using System.Security.Claims;
using PetBox.Core.Auth;
using PetBox.Core.Data;

namespace PetBox.Tests.Auth;

// A normal `project` claim authorizes only its own project; the cross-project wildcard
// "*" authorizes any; null/empty authorizes nothing.
public sealed class ProjectScopeTests
{
	[Theory]
	[InlineData("kpvotes", "kpvotes", true)]   // exact match
	[InlineData("kpvotes", "other", false)]    // mismatch
	[InlineData("*", "kpvotes", true)]          // wildcard -> any project
	[InlineData("*", "$system", true)]
	[InlineData("", "kpvotes", false)]          // empty claim -> denied
	[InlineData(null, "kpvotes", false)]        // missing claim -> denied
	public void Authorizes(string? claim, string projectKey, bool expected) =>
		ProjectScope.Authorizes(claim, projectKey).Should().Be(expected);
}

// The sandbox write gate (spec work/smoke-writes-into-real-projects):
//     Authorized = Authorizes(claim, projectKey) && (!sandboxOnly || catalog.IsSandboxAsync(projectKey))
// The containment check is ORTHOGONAL to the claim — it never reads `claim` — so a wildcard ("*")
// key does NOT bypass it. Table-driven over (claim, sandboxOnly, projectIsSandbox) so the wildcard
// case sits right next to the project-scoped one and the asymmetry is visible at a glance.
public sealed class ProjectScopeSandboxTests
{
	const string SandboxProj = "sandboxproj";
	const string RealProj = "realproj";

	static readonly StubCatalog Catalog = new(new HashSet<string>(StringComparer.Ordinal) { SandboxProj });

	[Theory]
	// sandboxOnly:false — identical to the plain Authorizes table; containment never applies.
	[InlineData("kpvotes", "kpvotes", false, true)]
	[InlineData("*", RealProj, false, true)]
	// sandboxOnly:true, project-scoped claim, matching claim+project.
	[InlineData(SandboxProj, SandboxProj, true, true)]   // sandbox project -> allowed
	[InlineData(RealProj, RealProj, true, false)]        // real project -> refused by containment
														 // sandboxOnly:true, WILDCARD claim — authorizes every project by IDENTITY, but containment
														 // still applies per-call. THE case the design calls out: "*" does not bypass the gate.
	[InlineData("*", SandboxProj, true, true)]           // wildcard + sandbox project -> allowed
	[InlineData("*", RealProj, true, false)]             // wildcard + real project -> STILL refused
														 // Identity still governs first: a project-scoped claim that doesn't even match the target is
														 // refused regardless of sandboxOnly/containment (short-circuit — Authorizes() runs first).
	[InlineData(SandboxProj, RealProj, true, false)]
	public async Task AuthorizesAsync_StringOverload(
		string? claim, string projectKey, bool sandboxOnly, bool expected)
	{
		(await ProjectScope.AuthorizesAsync(claim, projectKey, sandboxOnly, Catalog)).Should().Be(expected);
	}

	// A project that does not exist at all is NOT sandbox — the containment check denies rather
	// than throwing, same shape as any other unknown-project write.
	[Fact]
	public async Task AuthorizesAsync_UnknownProject_IsNotSandbox_SoSandboxOnlyKeyIsRefused() =>
		(await ProjectScope.AuthorizesAsync("*", "nosuchproject", sandboxOnly: true, Catalog))
			.Should().BeFalse();

	// The ClaimsPrincipal overload (the REST/MCP call path) reads BOTH claims off the principal and
	// defers to the string overload — same wildcard-does-not-bypass outcome, from the actual shape
	// ApiKeyAuthenticationHandler emits.
	[Fact]
	public async Task AuthorizesAsync_ClaimsOverload_WildcardSandboxOnlyKey_RefusedOnARealProject()
	{
		var user = Principal(claim: "*", sandboxOnly: true);
		(await ProjectScope.AuthorizesAsync(user, RealProj, Catalog)).Should().BeFalse();
		(await ProjectScope.AuthorizesAsync(user, SandboxProj, Catalog)).Should().BeTrue();
	}

	[Fact]
	public async Task AuthorizesAsync_ClaimsOverload_NoSandboxOnlyClaim_BehavesLikeThePlainCheck()
	{
		var user = Principal(claim: "*", sandboxOnly: false);
		(await ProjectScope.AuthorizesAsync(user, RealProj, Catalog)).Should().BeTrue();
	}

	// EvaluateAsync is AuthorizesAsync with the denial reason kept apart — same table as
	// AuthorizesAsync_StringOverload above, but asserting WHICH ProjectAccess came back rather than
	// collapsing both failure reasons into `false`. THE case the whole change exists for: a wildcard
	// claim authorizes RealProj by identity (ClaimMismatch would be wrong here), so a sandboxOnly
	// wildcard key refused on a non-sandbox project must report SandboxContainment, not ClaimMismatch.
	[Theory]
	[InlineData("kpvotes", "kpvotes", false, ProjectAccess.Allowed)]
	[InlineData("kpvotes", "other", false, ProjectAccess.ClaimMismatch)]
	[InlineData(SandboxProj, SandboxProj, true, ProjectAccess.Allowed)]
	[InlineData(RealProj, RealProj, true, ProjectAccess.SandboxContainment)]
	[InlineData("*", SandboxProj, true, ProjectAccess.Allowed)]
	[InlineData("*", RealProj, true, ProjectAccess.SandboxContainment)] // wildcard: identity says yes, containment says no
	[InlineData(SandboxProj, RealProj, true, ProjectAccess.ClaimMismatch)] // identity fails first, containment never runs
	public async Task EvaluateAsync_StringOverload_ReportsWhichCheckFailed(
		string? claim, string projectKey, bool sandboxOnly, ProjectAccess expected)
	{
		(await ProjectScope.EvaluateAsync(claim, projectKey, sandboxOnly, Catalog)).Should().Be(expected);
	}

	// The ClaimsPrincipal overload of EvaluateAsync, from the actual shape ApiKeyAuthenticationHandler
	// emits: a sandboxOnly WILDCARD key on a real (non-sandbox) project must come back
	// SandboxContainment, never ClaimMismatch — that distinction is the entire point of this change
	// (a wildcard smoke key refused here is refused for WHERE it's writing, not for its scope).
	[Fact]
	public async Task EvaluateAsync_ClaimsOverload_WildcardSandboxOnlyKey_OnARealProject_IsSandboxContainment_NotClaimMismatch()
	{
		var user = Principal(claim: "*", sandboxOnly: true);
		(await ProjectScope.EvaluateAsync(user, RealProj, Catalog)).Should().Be(ProjectAccess.SandboxContainment);
		(await ProjectScope.EvaluateAsync(user, SandboxProj, Catalog)).Should().Be(ProjectAccess.Allowed);
	}

	// A project-scoped key in a project it doesn't own is a ClaimMismatch even when sandboxOnly is
	// set — identity is checked first, so this must NOT read as SandboxContainment.
	[Fact]
	public async Task EvaluateAsync_ClaimsOverload_ProjectScopedKey_OnAnotherProject_IsClaimMismatch()
	{
		var user = Principal(claim: SandboxProj, sandboxOnly: true);
		(await ProjectScope.EvaluateAsync(user, RealProj, Catalog)).Should().Be(ProjectAccess.ClaimMismatch);
	}

	static ClaimsPrincipal Principal(string claim, bool sandboxOnly)
	{
		var claims = new List<Claim> { new("project", claim) };
		if (sandboxOnly) claims.Add(new Claim(ApiKeyAuthenticationHandler.SandboxOnlyClaim, "true"));
		return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
	}

	sealed class StubCatalog(HashSet<string> sandboxProjects) : IProjectCatalog
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
	}
}
