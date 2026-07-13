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
}
