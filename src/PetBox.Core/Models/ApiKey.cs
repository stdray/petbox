using LinqToDB.Mapping;

namespace PetBox.Core.Models;

[Table("ApiKeys")]
public sealed record ApiKey
{
	[PrimaryKey]
	public string Key { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public string Scopes { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }
	// Optional expiry. NULL = never expires (the default for normal keys). Set for temporary
	// agent/onboarding keys; the auth handler rejects the key once UtcNow passes this instant.
	public DateTime? ExpiresAt { get; init; }
	// The project a CROSS-PROJECT key ("*" ProjectKey) falls back to when a tool's optional
	// projectKey is omitted. The wildcard claim AUTHORIZES every project but SUPPLIES none, so
	// without this a "*" key must repeat projectKey on every call. NULL = no default (the old
	// behavior: an omitted projectKey is an error). Meaningless on a project-scoped key — it
	// already defaults to its own claim — so apikey_create rejects the combination.
	public string? DefaultProjectKey { get; init; }
	// A SandboxOnly key is a smoke/background-job key: ProjectScope's identity check (claim vs.
	// projectKey) still applies unchanged, but a SECOND, orthogonal containment check also has to
	// pass — the target project must have Project.Sandbox = true (ProjectScope.AuthorizesAsync).
	// The wildcard claim ("*") does NOT bypass this: it authorizes ANY project by claim, but a
	// SandboxOnly wildcard key still resolves the containment check against whatever projectKey
	// the call actually names. This is what lets one smoke key span every sandbox project without
	// also being able to reach $system / yobapub (spec work/smoke-writes-into-real-projects).
	public bool SandboxOnly { get; init; }
	// The last time this key successfully authenticated (spec apikey-last-used). COARSE BY DESIGN:
	// the auth hot path stamps an in-memory singleton (IKeyStatService) and KeyStatFlusher folds the
	// marks into this column about every 5 minutes, so the stored value trails reality by up to that
	// window — and up to a window's worth of marks is LOST on a hard restart (a graceful shutdown
	// flushes). Reads that must be fresh (apikey_list, the admin UI) merge this with the in-memory
	// value and take the later of the two. NULL = never used since the column existed (M043).
	public DateTime? LastUsedAt { get; init; }
	// WHO issued this key (spec access-attribution) — `user:<username>` for a mint from an admin
	// page, `key:<name>` for one through the apikey_create MCP verb, "system" for the node-agent
	// enroll/rotate path. The raw secret of the ISSUING key is never stored here, only its label.
	//
	// NULLABLE, and that is load-bearing rather than lazy: every key minted before M049 genuinely has
	// no recorded issuer, and NULL says exactly that. A default of "system" would have been a
	// FABRICATION — it would claim the operator minted keys nobody can account for, which is the
	// opposite of what an attribution column is for. ShareLink.CreatedBy defaults to "system" because
	// its rows are created by one code path; ApiKeys has five, so HealthEndpoint's nullable shape is
	// the honest precedent here.
	public string? CreatedBy { get; init; }
	// The FLEET HOST this key is bound to (M050, spec node-grant-own-carrier) — the deploy plane's
	// node id, on the node-agent keys minted by the enroll/rotate path. NULL on every other key, and
	// that is the whole point: a grant limited to ONE MACHINE now has a carrier of its own type
	// instead of borrowing ProjectKey, which names a TENANT.
	//
	// Before M050 the node id lived in ProjectKey, so a node `vdsina-1` and a project `vdsina-1` were
	// the same value in the same column and resolved through the same claim — the collision was
	// structural, not a naming convention anyone could tighten. Now they are DIFFERENT COLUMNS and
	// different claims (`host` vs `project`), so the collision cannot be expressed.
	//
	// A node key carries HostId and an EMPTY ProjectKey. Empty is not an oversight: ProjectScope
	// treats a blank claim as authorizing nothing at all, so the node key's reach over the tenant
	// axis is now nil rather than "whatever project happens to share the node's name".
	//
	// Not projected by ConfigApiKeyLookup: an appsettings-declared key is never a node key (node keys
	// are minted by the enroll/rotate path and rotated by it), so a config key always has HostId null
	// and can never claim to be a machine.
	public string? HostId { get; init; }
}
