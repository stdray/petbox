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

			return tenant.Kind == TenantKind.Project
				? Map(await ProjectScope.EvaluateAsync(projectClaim, tenant.Key, sandboxOnly, catalog, ct))
				: await KeyOnWorkspaceAsync(projectClaim, tenant.Key, ct);
		}

		return await UserAsync(principal, tenant, ct);
	}

	// "A project claim authorizes the workspace that project belongs to" — written ONCE, here. The
	// same rule is currently spelled out by hand in ConfigApi.AuthorizeWorkspaceAsync (project claim
	// -> Project.WorkspaceKey -> compare, wildcard passes) and, for the cookie half, by
	// WorkspaceRoleAuthorizationHandler; those call sites come out with the family enforcement, not
	// here, and until then this must agree with them on the allow/deny outcome.
	async Task<TenantAccess> KeyOnWorkspaceAsync(
		string? projectClaim, string workspaceKey, CancellationToken ct)
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

		// SANDBOX CONTAINMENT DOES NOT APPLY TO A WORKSPACE TARGET — the owner's decision, and the
		// reasoning is worth keeping because the opposite reads as the safer choice.
		//
		// The tempting version: a workspace carries no Sandbox flag and is broader than any project in
		// it, so containment "cannot be satisfied" and a sandboxOnly key is refused everywhere. That
		// would be STRICTER than anything the system does today — no existing workspace-target check
		// looks at the flag at all (ConfigApi.AuthorizeWorkspaceAsync doesn't; MemoryTools'
		// AssertMemoryProjectAsync doesn't) — so switching it on with the PEP would REFUSE keys that
		// work right now. Acceptance criterion 1 of work `authz-default-deny-delivery` ("ключ,
		// работавший до перехода, работает после") outranks the wish to be stricter, so the current
		// outcome is preserved deliberately rather than by omission.
		//
		// A PROJECT target is unaffected: ProjectScope.EvaluateAsync above still enforces containment
		// there, wildcard claim included, which is where the write gate was actually specified
		// (spec work/smoke-writes-into-real-projects). Tightening the workspace axis is a separate
		// decision with its own blast radius — shared memory, config, every workspace page.
		//
		// The flag is therefore not a parameter of this method at all: an unused one would read as an
		// oversight, and the next person would "fix" it.
		return TenantAccess.Allowed;
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
