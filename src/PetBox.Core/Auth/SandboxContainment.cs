using System.Security.Claims;
using PetBox.Core.Data;

namespace PetBox.Core.Auth;

// THE ONE HOME of sandbox containment for DERIVED storage — work
// `memory-container-sandbox-containment-bypass`.
//
// THE RULE THIS EXISTS TO STATE, because stating it as a count of code locations is what kept
// missing sites:
//
//     CONTAINMENT IS ENFORCED ON THE TARGET THE CALLER NAMES. Several surfaces then act on a
//     container they DERIVE from that target, and nothing authorizes the derived one.
//
// The named target is already covered — ProjectScope.EvaluateAsync applies containment to it, and
// the wildcard claim does not buy past it. What this file covers is the second hop. A surface that
// resolves `scope: "workspace"`, or reads a project's workspace canon alongside the project's own,
// or decodes a `$ws-<key>` pseudo-project, has moved to storage the PEP never judged. Every one of
// those hops must ask the question again, here, or it is a hole. Three were found this way, one at
// a time, before the rule was written down; with the rule written down they fall out mechanically.
//
// THE PREDICATE. A sandboxOnly key may touch storage only when that storage's Projects row carries
// Sandbox = true — the SAME catalog.IsSandboxAsync call ProjectScope.EvaluateAsync makes, so the
// derived hop and the named target cannot drift apart. Workspace memory containers ($workspace /
// $ws-<key>) are real Projects rows and are NOT sandbox rows, so a sandboxOnly key never reaches
// one: a container is the shared memory of a workspace that holds non-sandbox projects too, and
// handing it over discloses data provably outside the sandbox. A key/row that does not exist is
// not a sandbox either, so the default is denial.
//
// THE CALL SITES ARE NOT LISTED HERE, DELIBERATELY. The first edition of this file carried a
// numbered list of its three call sites and asked whoever added a fourth to extend it — a human
// count, the practice this codebase banned after three independent human counts of the MCP tool
// surface came back 92 / 100 / 122 when the truth was 97 (TenantEnforcementAllowlist.cs). The
// enumeration is MECHANICAL now: SandboxContainmentCallSiteGuardTests (work
// `sandbox-containment-callsite-ratchet`) sweeps the source for every file that derives, decodes or
// branches on a container key and holds a door that can act on one, and FAILS THE BUILD for any
// such site that does not route through Permits*Async below. The live list is regenerated on every
// test run into .tmp/sandbox-containment-callsites.txt — read it there; do not maintain one here.
//
// IDENTITY IS DECIDED BEFORE THIS, at every call site, exactly as ProjectScope.EvaluateAsync orders
// it: a caller with no claim on the tenant reads "not authorized", not "sandbox". Containment is
// the LAST question, asked only of a caller that was otherwise entitled.
public static class SandboxContainment
{
	// True when the principal is a sandboxOnly api key. Presence, not value — exactly how
	// ProjectScope and ApiKeyAuthenticationHandler read this claim.
	private static bool AppliesTo(ClaimsPrincipal? user) =>
		user?.HasClaim(c => c.Type == ApiKeyAuthenticationHandler.SandboxOnlyClaim) ?? false;

	// May this principal reach `storageKey`? `storageKey` is whatever actually gets read or written —
	// a project key, or a `$workspace` / `$ws-<key>` container key — NOT the tenant the caller named.
	// Passing the named target here instead of the derived storage is the mistake this whole file is
	// about.
	public static Task<bool> PermitsAsync(
		ClaimsPrincipal? user, string storageKey, IProjectCatalog catalog, CancellationToken ct = default) =>
		PermitsAsync(AppliesTo(user), storageKey, catalog, ct);

	// The string-shaped overload, for the decision point, which reads the flag off an identity rather
	// than a whole principal.
	private static async Task<bool> PermitsAsync(
		bool sandboxOnly, string storageKey, IProjectCatalog catalog, CancellationToken ct = default) =>
		!sandboxOnly || await catalog.IsSandboxAsync(storageKey, ct);

	// The container that represents a WORKSPACE as storage. A workspace has no Projects row and so no
	// Sandbox flag of its own; what it has is exactly one shared-memory container row, and that row is
	// what a workspace-targeted call ultimately reads or writes. Resolving it here rather than at each
	// call site is what lets every site ask one question of one predicate.
	public static Task<bool> PermitsWorkspaceAsync(
		bool sandboxOnly, string workspaceKey, IProjectCatalog catalog, CancellationToken ct = default) =>
		PermitsAsync(sandboxOnly, WorkspaceMemory.ContainerKeyFor(workspaceKey), catalog, ct);
}
