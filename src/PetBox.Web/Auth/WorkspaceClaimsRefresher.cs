using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PetBox.Core.Auth;
using PetBox.Core.Data;

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
// One indexed core-db read per authenticated request; the principal is cached per authentication
// by the cookie handler, so it is not re-run per authorize call.
public sealed class WorkspaceClaimsRefresher(ICoreDbFactory dbf) : IClaimsTransformation
{
	public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
	{
		var identity = principal.Identities.FirstOrDefault(i =>
			i.IsAuthenticated
			&& string.Equals(i.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal));
		if (identity is null)
			return Task.FromResult(principal);

		var userIdRaw = identity.FindFirst(PetBoxClaims.UserId)?.Value;
		if (!long.TryParse(userIdRaw, out var userId))
			return Task.FromResult(principal);

		using var db = dbf.Open();
		var memberships = db.WorkspaceMembers
			.Where(m => m.UserId == userId)
			.Select(m => new { m.WorkspaceKey, m.Role })
			.ToList();

		var fresh = WorkspaceRoleAuthorizationHandler.SerializeRoles(
			memberships.Select(m => (m.WorkspaceKey, m.Role)));

		// The active-workspace claim is a hint for navigation, not an authorization fact — but a
		// stale one (a workspace the user was removed from, or the old "$system" fallback) is
		// still misleading, so it is re-pinned to a workspace the user actually belongs to.
		var active = identity.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		var stillMember = active is not null
			&& memberships.Exists(m => string.Equals(m.WorkspaceKey, active, StringComparison.Ordinal));
		var freshActive = stillMember ? active : memberships.Count > 0 ? memberships[0].WorkspaceKey : null;

		var current = identity.FindFirst(PetBoxClaims.WorkspaceRoles)?.Value ?? "";
		if (string.Equals(current, fresh, StringComparison.Ordinal)
			&& string.Equals(active, freshActive, StringComparison.Ordinal))
			return Task.FromResult(principal);

		var claims = identity.Claims
			.Where(c => c.Type is not (PetBoxClaims.WorkspaceRoles or PetBoxClaims.ActiveWorkspace))
			.ToList();
		claims.Add(new Claim(PetBoxClaims.WorkspaceRoles, fresh));
		if (freshActive is not null)
			claims.Add(new Claim(PetBoxClaims.ActiveWorkspace, freshActive));

		var rebuilt = new ClaimsIdentity(claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
		return Task.FromResult(new ClaimsPrincipal(rebuilt));
	}
}
