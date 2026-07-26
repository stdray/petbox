using System.Security.Claims;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

// THE tenant access decision: (principal, tenant) -> decision. A normal injectable service with an
// explicit input — it reads nothing ambient (no IHttpContextAccessor, no route values, no "current
// user"), so the same decision is reachable from the endpoint pipeline, from the MCP dispatcher and
// from a test with no host at all, and it answers identically in all three.
//
// It is deliberately NOT part of the service layer. 12+ BackgroundServices reach the same services
// through CreateScope() with no principal and across every tenant at once; a check pushed down
// there would need a synthetic principal per job or a bypass, i.e. the same opt-in hole moved
// somewhere less visible.
public interface ITenantAuthorizer
{
	Task<TenantAccess> AuthorizeAsync(ClaimsPrincipal? principal, TenantRef tenant, CancellationToken ct = default);
}

// The outcome. Only Allowed vs "not Allowed" is contractual — the denial REASONS are diagnostics
// (they exist so a sandbox-contained key doesn't read as "wrong scope" and send the next agent
// chasing claims), not a response shape callers may branch on.
//
// NoTenant is reported ONLY for a syntactically absent target — a caller that named no tenant at
// all, which leaks nothing about what exists. A named-but-unknown tenant comes back NotAuthorized,
// indistinguishable from a wrong-tenant denial, so the decision point never becomes an existence
// oracle for another tenant's keys (the ordering McpProjectExistsFilter already protects).
public enum TenantAccess
{
	Allowed,
	NoTenant,
	NotAuthorized,
	SandboxContainment,
}

public sealed class TenantAuthorizer(IProjectCatalog catalog) : ITenantAuthorizer
{
	public async Task<TenantAccess> AuthorizeAsync(
		ClaimsPrincipal? principal, TenantRef tenant, CancellationToken ct = default)
	{
		// An unnamed tenant is a denial for EVERY principal, cross-project "*" included: the wildcard
		// authorizes any tenant, not the absence of one.
		if (!tenant.IsResolved) return TenantAccess.NoTenant;
		if (principal is null || !principal.Identities.Any(i => i.IsAuthenticated)) return TenantAccess.NotAuthorized;

		// Which credential answers is asked of the IDENTITY, not of claim presence: a policy that
		// admits two schemes MERGES both identities when both authenticate, so `principal.Identity`
		// (merely the first) is an ordering detail no authorization decision may rest on — the same
		// reasoning LogApi.AuthorizeProjectViewerAsync spells out. When a caller presents both an api
		// key and a session cookie, the KEY governs: that is the narrower of the two credentials, so
		// the answer is never broader than either one alone would give.
		var apiKey = principal.Identities.FirstOrDefault(i =>
			i.IsAuthenticated && string.Equals(i.AuthenticationType, ApiKeyAuthenticationHandler.SchemeName, StringComparison.Ordinal));

		if (apiKey is not null)
		{
			var projectClaim = apiKey.FindFirst(ApiKeyAuthenticationHandler.ProjectClaim)?.Value;
			// Presence, not value — exactly how ProjectScope reads it.
			var sandboxOnly = apiKey.HasClaim(c => c.Type == ApiKeyAuthenticationHandler.SandboxOnlyClaim);

			// BOTH kinds of target answer the sandbox question. The split is over how the tenant is
			// IDENTIFIED, not over which rules apply to it — a split that let one kind skip a rule is
			// exactly the defect work `memory-container-sandbox-containment-bypass` records.
			return tenant.Kind == TenantKind.Project
				? Map(await ProjectScope.EvaluateAsync(projectClaim, tenant.Key, sandboxOnly, catalog, ct))
				: await KeyOnWorkspaceAsync(projectClaim, tenant.Key, sandboxOnly, ct);
		}

		return await UserAsync(principal, tenant, ct);
	}

	// "A project claim authorizes the workspace that project belongs to" — written ONCE, here. The
	// same rule is currently spelled out by hand in ConfigApi.AuthorizeWorkspaceAsync (project claim
	// -> Project.WorkspaceKey -> compare, wildcard passes) and, for the cookie half, by
	// WorkspaceRoleAuthorizationHandler; those call sites come out with the family enforcement, not
	// here, and until then this must agree with them on the IDENTITY half of the outcome.
	//
	// It deliberately does NOT agree with them on containment: those hand-written checks never look at
	// `sandboxOnly` at all, which is the very gap this method now closes. Bringing this into line with
	// them would mean re-opening the leak to close a diff — see the containment note below.
	async Task<TenantAccess> KeyOnWorkspaceAsync(
		string? projectClaim, string workspaceKey, bool sandboxOnly, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(projectClaim)) return TenantAccess.NotAuthorized;

		if (projectClaim != ProjectScope.AllProjects)
		{
			var claimWorkspace = await catalog.WorkspaceKeyOfAsync(projectClaim, ct);
			// IsNullOrEmpty, not `is null`: a Project row's WorkspaceKey defaults to "" in the model,
			// and "" must never match a target that reached here as a resolved tenant.
			if (string.IsNullOrEmpty(claimWorkspace) || !string.Equals(claimWorkspace, workspaceKey, StringComparison.Ordinal))
				return TenantAccess.NotAuthorized;
		}

		// SANDBOX CONTAINMENT ON A WORKSPACE TARGET: a sandboxOnly key is REFUSED, always.
		//
		// What containment asks is "is the target inside the sandbox?", and the only evidence this
		// system has for that is Project.Sandbox — a flag on a PROJECT row. A workspace does not
		// merely lack that flag; it is the wrong SHAPE to carry one. A workspace strictly contains
		// its projects, sandbox and non-sandbox alike, and its shared-memory container ($workspace /
		// $ws-<key>) is fed by all of them, so granting it to a sandboxOnly key hands over data that
		// provably lives outside the sandbox. There is therefore no workspace for which containment
		// could be satisfied, and the honest answer is a denial rather than a pass. SandboxContainment
		// — not NotAuthorized — because the claim is fine and the sandbox restriction is what refused.
		//
		// THIS REVERSES THE PREVIOUS DECISION, and the reversal is the whole point of work
		// `memory-container-sandbox-containment-bypass`. This branch used to return Allowed, arguing
		// that enforcing containment here would be stricter than anything else in the tree (true — no
		// hand-written workspace check reads the flag) and would break acceptance criterion 1 of work
		// `authz-default-deny-delivery`, "a key that worked before works after". That criterion was
		// applied to something it does not cover: measured against production on 2026-07-25, a
		// wildcard+sandboxOnly key read `$workspace` shared memory — another tenant's facts — while
		// the SAME key was correctly refused `$system` on the project axis. Two doors into one room,
		// one of them unlocked. What the criterion protects is working CAPABILITY; a leak is not one,
		// so "it worked before" is not an argument for keeping it. DO NOT RESTORE THE OLD BEHAVIOUR:
		// re-opening this branch to Allowed re-opens the cross-tenant read, and the pin in
		// TenantAuthorizerTests records the same reasoning next to the case that proves it.
		//
		// IDENTITY IS STILL DECIDED FIRST (above), exactly as ProjectScope.EvaluateAsync orders it, so
		// a key aimed at a workspace it does not belong to still reads NotAuthorized and the denial
		// reason keeps naming the real problem.
		//
		// THIS DECISION IS NOT SUFFICIENT ON ITS OWN, and that is worth knowing here because the
		// obvious reading of "one decision point" is that it must be. This branch only sees a target
		// the caller NAMED. Other surfaces then DERIVE a container from that target and act on it,
		// which nothing here judges — the memory verbs' `scope: "workspace"` redirect and the canon
		// endpoint's workspace leg both do, and both were measured leaking with this branch already
		// fixed. SandboxContainment is the shared home of the rule — read it before adding a site that
		// derives a container. It deliberately does NOT list its call sites: the enumeration is
		// mechanical (SandboxContainmentCallSiteGuardTests fails the build on a deriving site that does
		// not route through the predicate, and regenerates the live list into
		// .tmp/sandbox-containment-callsites.txt).
		return await SandboxContainment.PermitsWorkspaceAsync(sandboxOnly, workspaceKey, catalog, ct)
			? TenantAccess.Allowed
			: TenantAccess.SandboxContainment;
	}

	// The cookie half of the same boundary. A signed-in user carries no `project` claim, so "may this
	// principal touch this tenant?" is "is it a member of the tenant's workspace?" — for a PROJECT
	// target the workspace is resolved from the catalog, never from the URL, which is what makes a
	// wsA-shaped URL pointed at a wsB project answer on wsB's membership.
	//
	// Membership is the TENANT question only. WHICH role a surface demands (Admin vs Member vs
	// Viewer) is a different axis and stays where it is — this returns Allowed for any membership,
	// the same threshold LogApi's cookie live-tail path uses.
	async Task<TenantAccess> UserAsync(ClaimsPrincipal principal, TenantRef tenant, CancellationToken ct)
	{
		var workspaceKey = tenant.Kind == TenantKind.Workspace
			? tenant.Key
			: await catalog.WorkspaceKeyOfAsync(tenant.Key, ct);

		if (string.IsNullOrEmpty(workspaceKey)) return TenantAccess.NotAuthorized;

		// Sysadmin free-pass lives inside HasWorkspaceRoleAtLeast — same claim, same reading as the
		// authorization pipeline, so the two cannot drift.
		return principal.HasWorkspaceRoleAtLeast(workspaceKey, WorkspaceRole.Viewer)
			? TenantAccess.Allowed
			: TenantAccess.NotAuthorized;
	}

	static TenantAccess Map(ProjectAccess access) => access switch
	{
		ProjectAccess.Allowed => TenantAccess.Allowed,
		ProjectAccess.SandboxContainment => TenantAccess.SandboxContainment,
		_ => TenantAccess.NotAuthorized,
	};
}
