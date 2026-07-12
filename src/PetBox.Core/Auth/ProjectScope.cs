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
}
