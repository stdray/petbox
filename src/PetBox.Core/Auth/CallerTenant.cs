using System.Security.Claims;

namespace PetBox.Core.Auth;

// The tenant a caller carries on itself — what `TenantSource.CallerDefault` names.
//
// Lives in Core, and is the ONLY reading of it: the MCP surface asks the same question through
// ModuleMcp.DefaultProjectOf (which delegates here) for argument injection and existence checking,
// and the endpoint PEP asks it for a CallerDefault declaration. A second copy would be a second
// answer to "which project is this key's own?", and the two would disagree on exactly the keys that
// matter (a cross-project key with a default, whose claim says "*" and whose default says otherwise).
public static class CallerTenant
{
	// The project the caller falls back to when it names none: its own `project` claim, or — for a
	// cross-project ("*") key, whose claim authorizes every project but names none — the
	// `project_default` claim. null when nothing resolves, which every caller must read as a refusal
	// rather than as "all of them".
	//
	// A cookie-authenticated user has no `project` claim at all and therefore no default: a signed-in
	// human is a member of workspaces, not the owner of one project.
	public static string? DefaultProjectOf(ClaimsPrincipal? user) =>
		user?.Claims.FirstOrDefault(c => c.Type == ApiKeyAuthenticationHandler.ProjectClaim)?.Value switch
		{
			null or "" => null,
			ProjectScope.AllProjects =>
				Blank(user.Claims.FirstOrDefault(c => c.Type == ApiKeyAuthenticationHandler.DefaultProjectClaim)?.Value),
			var single => Blank(single),
		};

	static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
