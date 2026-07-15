using System.Security.Claims;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

// Read-side helpers for the workspace-role claims a signed-in cookie identity carries
// (yb:sysadmin + yb:ws_roles "ws=Role,ws=Role"). The authorization pipeline enforces these
// server-side via WorkspaceRoleRequirement; these helpers let a view decide whether to render
// a link into an admin-gated page at all, so a non-admin never sees a dead link that would
// redirect to /Login.
public static class WorkspaceRoleClaims
{
	// True when the principal may administer the given workspace: a sysadmin (implicitly admins
	// every workspace) or the holder of the Admin role for that specific workspace.
	public static bool CanAdminWorkspace(this ClaimsPrincipal user, string workspaceKey)
	{
		if (user.HasClaim(PetBoxClaims.IsSysAdmin, "true"))
			return true;
		return GetWorkspaceRole(user, workspaceKey) == WorkspaceRole.Admin;
	}

	// True when the principal holds AT LEAST minRole in the given workspace (sysadmin free-pass;
	// lower ordinal = stronger role — see WorkspaceRole). For a page whose class-wide [Authorize]
	// policy is intentionally the WEAKEST role reachable on the page (WorkspaceViewer, so a Viewer
	// can read a board/log/trace) but one specific POST handler on that same page is a MUTATION
	// that needs Member+ (workspace-access-isolation follow-up: viewer-member-consistency). Reads
	// the SAME yb:ws_roles claim WorkspaceRoleAuthorizationHandler checks — kept fresh per request
	// by WorkspaceClaimsRefresher, so this reflects current membership, not a stale sign-in snapshot.
	public static bool HasWorkspaceRoleAtLeast(this ClaimsPrincipal user, string workspaceKey, WorkspaceRole minRole)
	{
		if (user.HasClaim(PetBoxClaims.IsSysAdmin, "true"))
			return true;
		return GetWorkspaceRole(user, workspaceKey) is { } role && role <= minRole;
	}

	// Every workspace key recorded in yb:ws_roles (roles discarded) — the set of workspaces the caller
	// currently belongs to. The claim is WorkspaceClaimsRefresher's per-request materialisation of the
	// WorkspaceMembers table, so this is as fresh as the authorization pipeline's own view (the same
	// source CanAdminWorkspace / HasWorkspaceRoleAtLeast read). EMPTY when the claim is absent (a
	// non-cookie identity the refresher never touched): absence is "unknown", not "no memberships", so a
	// caller that must not over- or under-scope has to fall back to IWorkspaceMembershipService. The
	// "ws=Role,ws=Role" wire format is owned by WorkspaceRoleRequirement.SerializeRoles.
	public static IReadOnlyCollection<string> MemberWorkspaceKeys(this ClaimsPrincipal user)
	{
		var claim = user.FindFirst(PetBoxClaims.WorkspaceRoles)?.Value;
		if (string.IsNullOrEmpty(claim))
			return [];

		var keys = new HashSet<string>(StringComparer.Ordinal);
		foreach (var pair in claim.Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			var eq = pair.IndexOf('=', StringComparison.Ordinal);
			if (eq > 0) keys.Add(pair[..eq]);
		}
		return keys;
	}

	// The principal's role in the given workspace, or null if none is recorded in the claim.
	public static WorkspaceRole? GetWorkspaceRole(this ClaimsPrincipal user, string workspaceKey)
	{
		var claim = user.FindFirst(PetBoxClaims.WorkspaceRoles)?.Value;
		if (string.IsNullOrEmpty(claim))
			return null;

		foreach (var pair in claim.Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = pair.Split('=');
			if (parts.Length != 2) continue;
			if (!string.Equals(parts[0], workspaceKey, StringComparison.Ordinal)) continue;
			if (Enum.TryParse<WorkspaceRole>(parts[1], ignoreCase: true, out var role))
				return role;
		}
		return null;
	}
}
