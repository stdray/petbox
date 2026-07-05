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
