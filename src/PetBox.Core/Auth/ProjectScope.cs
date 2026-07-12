using System.Security.Claims;
using PetBox.Core.Data;

namespace PetBox.Core.Auth;

// Single decision point for "does this API key's `project` claim authorize access to
// `projectKey`?". A normal key carries one project and matches it exactly; a CROSS-PROJECT
// key carries the wildcard "*" (minted on the sysadmin agent-keys page) and authorizes any
// project — for PetBox devs to monitor and operate across all projects. The wildcard lives
// only in the claim; tools still read/write under the real requested projectKey, so "*"
// never becomes a storage path.
public static class ProjectScope
{
	// Sentinel `project` value for a cross-project key.
	public const string AllProjects = "*";

	// A BLANK projectKey is not a project and is authorized by nothing — not even by "*". The
	// wildcard claim used to say yes to it (`Authorizes("*", "")` was true), and every store below
	// names its file after the key it is handed, so an empty string walked straight into a literal
	// `tasks/.db` / `memory/.db`. Blank means ABSENT everywhere (ModuleMcp.ResolveProject reads it
	// that way); the one place it could still reach storage is closed here, at the single decision
	// point, so no caller has to remember to pre-check it.
	public static bool Authorizes(string? projectClaim, string projectKey) =>
		!string.IsNullOrEmpty(projectClaim) &&
		!string.IsNullOrWhiteSpace(projectKey) &&
		(projectClaim == AllProjects || string.Equals(projectClaim, projectKey, StringComparison.Ordinal));

	// The sandbox write gate (spec work/smoke-writes-into-real-projects):
	//
	//     Authorized(claim, projectKey, sandboxOnly) =
	//         Authorizes(claim, projectKey)                              // identity, unchanged above
	//         && (!sandboxOnly || await catalog.IsSandboxAsync(projectKey))   // containment, NEW
	//
	// The containment check is orthogonal to the claim: it does not read `claim` at all, so the
	// wildcard claim ("*") — which authorizes every projectKey by identity — does NOT bypass it. A
	// SandboxOnly "*" key still has to land in a project with Project.Sandbox = true; that is
	// deliberate (one smoke key spanning many sandbox projects), not a hole in the wildcard.
	public static async Task<bool> AuthorizesAsync(
		string? projectClaim, string projectKey, bool sandboxOnly, IProjectCatalog catalog, CancellationToken ct = default) =>
		Authorizes(projectClaim, projectKey) && (!sandboxOnly || await catalog.IsSandboxAsync(projectKey, ct));

	// Claims-carrying overload for the ASP.NET request path (REST handlers, ModuleMcp): reads the
	// `project` and `sandbox_only` claims off `user` (the same claims ApiKeyAuthenticationHandler
	// emits) and defers to the string-based overload above. Every REST/MCP call site that used to
	// call the sync `Authorizes(claim, projectKey)` against a ClaimsPrincipal should call this
	// instead — it is a strict superset (identity check unchanged, containment check added).
	public static Task<bool> AuthorizesAsync(
		ClaimsPrincipal? user, string projectKey, IProjectCatalog catalog, CancellationToken ct = default)
	{
		var claim = user?.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		var sandboxOnly = user?.Claims.Any(c => c.Type == ApiKeyAuthenticationHandler.SandboxOnlyClaim) ?? false;
		return AuthorizesAsync(claim, projectKey, sandboxOnly, catalog, ct);
	}
}
