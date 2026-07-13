using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PetBox.Core.Auth;

namespace PetBox.Web.Auth;

// Membership is a DB fact, so the cookie's membership claims must never be the source of truth.
// They used to be: yb:ws_roles / yb:ws were written ONCE, at sign-in (Login.cshtml.cs), and never
// rebuilt — so an admin adding a user to a workspace (the normal invite path!) changed nothing
// until that user signed out and back in, and a REMOVED member kept their access for the life of
// the cookie (7 days). Rebuilding them per request from WorkspaceMembers makes both directions
// take effect immediately, and keeps WorkspaceRoleAuthorizationHandler's claim-based check honest
// (it now reads a claim that was materialised from the DB on this very request).
//
// Chosen over "re-SignIn on every membership mutation" because that only fixes the mutations we
// remember to hook (UI members page today; MCP/REST/seed/migration tomorrow) and cannot fix a
// cookie the mutating request does not own — the victim's session. This one closes both.
//
// Cookie identities only: the ApiKey scheme carries its own scope claims and never has yb:user_id.
// One indexed membership read per authenticated request; the principal is cached per authentication
// by the cookie handler, so it is not re-run per authorize call.
//
// The read goes through IWorkspaceMembershipService — this is an IClaimsTransformation, i.e. pipeline
// code, and the DB is the service layer's alone. That also fixed a real defect: this ran core.db
// SYNCHRONOUSLY (.ToList() behind Task.FromResult), blocking a request thread on SQLite on EVERY
// authenticated request. It now awaits.
public sealed class WorkspaceClaimsRefresher(IWorkspaceMembershipService memberships) : IClaimsTransformation
{
	public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
	{
		var identity = principal.Identities.FirstOrDefault(i =>
			i.IsAuthenticated
			&& string.Equals(i.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal));
		if (identity is null)
			return principal;

		var userIdRaw = identity.FindFirst(PetBoxClaims.UserId)?.Value;
		if (!long.TryParse(userIdRaw, out var userId))
			return principal;

		var roles = await memberships.GetRolesAsync(userId);

		var fresh = WorkspaceRoleAuthorizationHandler.SerializeRoles(
			roles.Select(m => (m.WorkspaceKey, m.Role)));

		// The active-workspace claim is a hint for navigation, not an authorization fact — but a
		// stale one (a workspace the user was removed from, or the old "$system" fallback) is
		// still misleading, so it is re-pinned to a workspace the user actually belongs to.
		var active = identity.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		var stillMember = active is not null
			&& roles.Any(m => string.Equals(m.WorkspaceKey, active, StringComparison.Ordinal));
		var freshActive = stillMember ? active : roles.Count > 0 ? roles[0].WorkspaceKey : null;

		var current = identity.FindFirst(PetBoxClaims.WorkspaceRoles)?.Value ?? "";
		if (string.Equals(current, fresh, StringComparison.Ordinal)
			&& string.Equals(active, freshActive, StringComparison.Ordinal))
			return principal;

		var claims = identity.Claims
			.Where(c => c.Type is not (PetBoxClaims.WorkspaceRoles or PetBoxClaims.ActiveWorkspace))
			.ToList();
		claims.Add(new Claim(PetBoxClaims.WorkspaceRoles, fresh));
		if (freshActive is not null)
			claims.Add(new Claim(PetBoxClaims.ActiveWorkspace, freshActive));

		var rebuilt = new ClaimsIdentity(claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
		return new ClaimsPrincipal(rebuilt);
	}
}
