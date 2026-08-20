using System.Security.Claims;

namespace PetBox.Core.Auth;

// WHO is issuing (or re-scoping) an API key, and therefore WHICH scopes they may put on it.
//
// This is the type the defect `workspaceadmin-self-issue-admin-provision-root` was missing. The
// mint path used to answer only "is this scope a KNOWN string?" (ApiKeyScopes.Validate) and never
// "may THIS caller hand it out?", so a WorkspaceAdmin — which any self-service tenant becomes on
// creating a workspace — could tick `admin:provision` on their own project's key and walk out with
// cross-tenant root. Tenancy was being mistaken for capability.
//
// ONE READING, DERIVED FROM THE PRINCIPAL, so the four issuing surfaces (two admin pages that mint,
// two that re-scope, plus the MCP verbs) cannot each invent their own answer — the way the tenant
// axis has exactly one reading in CallerTenant.
public sealed record KeyIssuer(string Actor, bool MayGrantPrivileged)
{
	// The system's own hand: migrations, enrollment, background jobs. Not reachable from a request —
	// there is no principal to derive it from, so nothing a caller sends can produce it.
	public static KeyIssuer System { get; } = new("system", MayGrantPrivileged: true);

	// THE RULE, and the whole of it:
	//
	//   * a cookie session carrying IsSysAdmin=true          -> may grant privileged (the operator);
	//   * an api key that itself holds `admin:provision`     -> may grant privileged (the operator's
	//     provisioning key — it can already mint such a key by definition, so refusing here would
	//     block onboarding without closing anything);
	//   * everything else, WorkspaceAdmin included           -> may not.
	//
	// WHY WorkspaceAdmin IS NOT ON THE LIST even though it is an "admin": the role is a TENANT role.
	// It says the holder governs a workspace, which is precisely the boundary a privileged scope
	// escapes. An admin of one tenant handing out fleet-wide reach is the escalation, not an
	// incidental consequence of it.
	//
	// A cookie user who is NOT a sysadmin and an anonymous principal both land in the same place, and
	// so does a key with no `admin:provision`: the default is refusal. Callers that legitimately need
	// the free pass have to be able to prove it from a claim.
	public static KeyIssuer From(ClaimsPrincipal? user)
	{
		if (user is null)
			return new KeyIssuer("anonymous", MayGrantPrivileged: false);

		// A COOKIE user first: a signed-in human is described by their username, never by a key. Note
		// this branch is reached for a non-sysadmin admin too — they get an actor string and no pass.
		var username = user.Identity?.IsAuthenticated == true ? user.Identity.Name : null;
		if (!string.IsNullOrWhiteSpace(username) && !IsApiKeyPrincipal(user))
			return new KeyIssuer($"user:{username}", user.HasClaim(PetBoxClaims.IsSysAdmin, "true"));

		// An API-KEY caller. `key_name` is the key's human label (ApiKeyAuthenticationHandler emits
		// it); the raw secret is never part of the actor string, so an attribution row can be read by
		// anyone who may read the key list without handing them a live credential.
		if (IsApiKeyPrincipal(user))
		{
			var name = user.FindFirst(ApiKeyAuthenticationHandler.KeyNameClaim)?.Value;
			var actor = string.IsNullOrWhiteSpace(name) ? "key:(unnamed)" : $"key:{name.Trim()}";
			return new KeyIssuer(actor, ApiKeyScopes.Granted(user, ApiKeyScopes.AdminProvision));
		}

		// A sysadmin whose identity carries no name (test principals, future schemes) still gets the
		// pass the claim grants — the claim is the authority, the name is only the label.
		return user.HasClaim(PetBoxClaims.IsSysAdmin, "true")
			? new KeyIssuer("user:(sysadmin)", MayGrantPrivileged: true)
			: new KeyIssuer("anonymous", MayGrantPrivileged: false);
	}

	// An api-key principal is the one carrying a `project` claim — the claim ApiKeyAuthenticationHandler
	// ALWAYS emits and a cookie identity never has (CallerTenant says the same thing from the other
	// side: "a signed-in human is a member of workspaces, not the owner of one project").
	static bool IsApiKeyPrincipal(ClaimsPrincipal user) =>
		user.FindFirst(ApiKeyAuthenticationHandler.ProjectClaim) is not null;

	// The scope reading used to be a THIRD private copy right here, with the comment "Ordinal, like
	// the catalog: scope values are identifiers, not prose." The comment was right and the copy was
	// the problem — it stated the catalog's rule instead of asking the catalog for it, which is how
	// ScopeAuthorizationHandler could hold a different one without anything noticing. It is now
	// ApiKeyScopes.Granted, so this file no longer has a scope-comparison of its own to drift.
}
