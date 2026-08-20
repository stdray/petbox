namespace PetBox.Core.Auth;

#pragma warning disable CA1724
public static class PetBoxClaims
#pragma warning restore CA1724
{
	public const string UserId = "yb:user_id";
	public const string ActiveWorkspace = "yb:ws";

	// ── TWO DEAD LETTERS, KEPT ON PURPOSE (work `scope-claims-canonicalization`) ──────────────────
	//
	// These two never described the wire. `ApiKeyAuthenticationHandler` has always emitted the tenant
	// and grant claims under the BARE names `project` and `scopes`, and every reader in the codebase
	// has always read them under those bare names — so `yb:project`/`yb:scopes` matched nothing, ever.
	// They sat here unused (grep found zero call sites) looking exactly like the canon they were not.
	//
	// THE TRAP THEY SET, spelled out because it is a silent one: "these constants exist, let us
	// finally use them" is a one-line change that compiles, passes review, and denies EVERY live api
	// key — the claim a reader then looks for is simply absent from the token. Do not substitute them.
	// The real canon is `ApiKeyAuthenticationHandler.ProjectClaim` / `.ScopesClaim`.
	//
	// KEPT rather than deleted, deliberately: the names stay greppable, so anyone who meets
	// `yb:project` in an old branch, a log line or a note lands on this comment instead of on nothing.
	// The other four members of this class are live and unaffected — they belong to the COOKIE
	// identity, which is a different credential with a different claim set.
	[Obsolete("Never emitted. The api-key tenant claim is bare \"project\" — use " +
		"ApiKeyAuthenticationHandler.ProjectClaim. Substituting this constant denies every live key.")]
	public const string ProjectKey = "yb:project";

	[Obsolete("Never emitted. The api-key grant claim is bare \"scopes\" — use " +
		"ApiKeyAuthenticationHandler.ScopesClaim, or ask ApiKeyScopes.Granted(user, scope). " +
		"Substituting this constant denies every live key.")]
	public const string Scopes = "yb:scopes";

	public const string WorkspaceRoles = "yb:ws_roles";
	public const string IsSysAdmin = "yb:sysadmin";
}
