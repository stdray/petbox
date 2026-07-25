namespace PetBox.Core.Auth;

// WHICH tenant a request is asking to touch. The access boundary is ONE boundary with two kinds of
// target (spec authz-tenant-default-deny): a project, or a workspace. Not two subsystems — a
// caller's claim authorizes a workspace exactly when it authorizes a project belonging to it, so
// both kinds are answered by one decision point (ITenantAuthorizer) against one reference type.
public enum TenantKind
{
	Project,
	Workspace,
}

// An EXPLICIT tenant target, constructed by the caller and passed by value. It carries no ambient
// context: no HttpContext, no route values, no "current" anything. Whoever knows which tenant a
// surface acts on says so here; the decision point never goes looking.
//
// The spec does NOT promise how the target is ENCODED on the wire — the same tenant is written
// today as a `workspaceKey` parameter (config) and as a `$ws-<key>` pseudo-project (shared memory).
// Both encodings keep working: they are two ways to reach `TenantRef.Workspace` / `TenantRef.Project`,
// and unifying them is separate work.
public readonly record struct TenantRef
{
	readonly string? _key;

	TenantRef(TenantKind kind, string? key)
	{
		Kind = kind;
		_key = key?.Trim() ?? string.Empty;
	}

	public TenantKind Kind { get; }

	// Never null, including on `default(TenantRef)` (which runs no constructor) — the field is
	// nullable so the default is EMPTY rather than a lie about having been set.
	public string Key => _key ?? string.Empty;

	// A blank target is not a tenant. `default(TenantRef)` is therefore unresolved, which is what
	// makes forgetting to name a tenant a DENIAL rather than a hole: the zero value of the type is
	// already the deny case, before anyone writes a check.
	public bool IsResolved => !string.IsNullOrWhiteSpace(Key);

	public static TenantRef Project(string? projectKey) => new(TenantKind.Project, projectKey);

	public static TenantRef Workspace(string? workspaceKey) => new(TenantKind.Workspace, workspaceKey);

	public override string ToString() =>
		$"{(Kind == TenantKind.Workspace ? "workspace" : "project")}:{Key}";
}
