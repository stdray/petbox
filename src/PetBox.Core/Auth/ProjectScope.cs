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

	public static bool Authorizes(string? projectClaim, string projectKey) =>
		!string.IsNullOrEmpty(projectClaim) &&
		(projectClaim == AllProjects || string.Equals(projectClaim, projectKey, StringComparison.Ordinal));
}
