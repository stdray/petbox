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
	//
	// bool-returning form kept for callers that only need the yes/no (most call sites just gate on
	// it). Callers that need to explain WHY a denial happened — so a wildcard smoke key refused by
	// containment doesn't read as "not scoped" and send the next agent chasing claims instead of the
	// target project — should call EvaluateAsync below instead; it is the same check, refined.
	public static async Task<bool> AuthorizesAsync(
		string? projectClaim, string projectKey, bool sandboxOnly, IProjectCatalog catalog, CancellationToken ct = default) =>
		await EvaluateAsync(projectClaim, projectKey, sandboxOnly, catalog, ct) == ProjectAccess.Allowed;

	// Claims-carrying overload for the ASP.NET request path (REST handlers, ModuleMcp): reads the
	// `project` and `sandbox_only` claims off `user` (the same claims ApiKeyAuthenticationHandler
	// emits) and defers to the string-based overload above. Every REST/MCP call site that used to
	// call the sync `Authorizes(claim, projectKey)` against a ClaimsPrincipal should call this
	// instead — it is a strict superset (identity check unchanged, containment check added).
	public static Task<bool> AuthorizesAsync(
		ClaimsPrincipal? user, string projectKey, IProjectCatalog catalog, CancellationToken ct = default) =>
		AuthorizesAsync(ClaimOf(user), projectKey, SandboxOnlyOf(user), catalog, ct);

	// EvaluateAsync is AuthorizesAsync with the denial reason kept apart instead of collapsed into a
	// bool. Same two-step formula, same short-circuit (a claim mismatch is reported even when
	// sandboxOnly containment would ALSO have failed — identity is checked first, exactly as
	// AuthorizesAsync does) — it just returns which step failed so a caller can say something truer
	// than "ApiKey is not scoped" when the real problem is that a sandboxOnly key hit a non-sandbox
	// project (the wildcard-claim case: identity says yes, containment says no).
	public static async Task<ProjectAccess> EvaluateAsync(
		string? projectClaim, string projectKey, bool sandboxOnly, IProjectCatalog catalog, CancellationToken ct = default)
	{
		if (!Authorizes(projectClaim, projectKey)) return ProjectAccess.ClaimMismatch;
		if (sandboxOnly && !await catalog.IsSandboxAsync(projectKey, ct)) return ProjectAccess.SandboxContainment;
		return ProjectAccess.Allowed;
	}

	// Claims-carrying overload of EvaluateAsync, mirroring the AuthorizesAsync(ClaimsPrincipal, ...)
	// overload above.
	public static Task<ProjectAccess> EvaluateAsync(
		ClaimsPrincipal? user, string projectKey, IProjectCatalog catalog, CancellationToken ct = default) =>
		EvaluateAsync(ClaimOf(user), projectKey, SandboxOnlyOf(user), catalog, ct);

	static string? ClaimOf(ClaimsPrincipal? user) => user?.Claims.FirstOrDefault(c => c.Type == "project")?.Value;

	static bool SandboxOnlyOf(ClaimsPrincipal? user) =>
		user?.Claims.Any(c => c.Type == ApiKeyAuthenticationHandler.SandboxOnlyClaim) ?? false;

	// The one place that spells out what a ProjectAccess.SandboxContainment denial means and what to
	// do about it, so ModuleMcp (MCP tool errors) and LogApi (REST ingest ErrorResponse bodies) say
	// the same thing with only the caller's usual noun for the key swapped in ("ApiKey" vs "key").
	public static string SandboxDenialMessage(string projectKey, string subject = "ApiKey") =>
		$"{subject} is sandboxOnly and can write only into sandbox projects (Project.Sandbox=true); " +
		$"'{projectKey}' is not one. A sandboxOnly key targets the sandbox project instead (see AGENTS.md rule 7).";
}

// The reason ProjectScope.EvaluateAsync denied access — kept apart from a bare bool so callers can
// tell "the claim doesn't cover this project" from "the claim covers it, but this project isn't a
// sandbox and the key is sandboxOnly" instead of collapsing both into one message.
public enum ProjectAccess
{
	Allowed,
	ClaimMismatch,
	SandboxContainment,
}
