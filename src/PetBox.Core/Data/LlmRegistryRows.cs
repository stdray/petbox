using LinqToDB.Mapping;

namespace PetBox.Core.Data;

// Storage rows for the LLM registry that lives in core.db (M039_LlmRegistry). Plain columns —
// Capability/Thinking/Scope are strings here, not enums: PetBox.Core must not depend on the
// router's contract assembly, so the enum parsing happens in PetBox.LlmRouter where the contract
// types live. Scope holds a PetBox.Core.Settings.Scope name ("System"/"Workspace"; "Project" is
// reserved and unwritten today).

// An endpoint AND its api key, in ONE row. That is deliberate: the key is a column of the
// endpoint, not a separate entity addressed by name, so "an endpoint whose key lives somewhere
// else / nowhere" is not a representable state. KeyCipher/KeyIv/KeyAuthTag are the AES-GCM triple
// from ISecretEncryptor; all three NULL means a deliberately keyless endpoint (local, no auth),
// all three set means an authenticated one, and any half-filled combination is a corrupt row.
[Table("llm_endpoints")]
public sealed record LlmEndpointRow
{
	[Column, PrimaryKey(0), NotNull] public string Scope { get; init; } = string.Empty;
	[Column, PrimaryKey(1), NotNull] public string ScopeKey { get; init; } = string.Empty;
	[Column, PrimaryKey(2), NotNull] public string Name { get; init; } = string.Empty;

	[Column, NotNull] public string BaseUrl { get; init; } = string.Empty;
	[Column, Nullable] public string? CertThumbprint { get; init; }
	[Column, NotNull] public int ConnectTimeoutMs { get; init; }
	[Column, NotNull] public int RequestTimeoutMs { get; init; }

	[Column, Nullable] public string? KeyCipher { get; init; }
	[Column, Nullable] public string? KeyIv { get; init; }
	[Column, Nullable] public string? KeyAuthTag { get; init; }

	[Column, NotNull] public DateTime UpdatedAt { get; init; }
	[Column, Nullable] public long? UpdatedBy { get; init; }
}

// One link in a capability's provider chain, pinned to the level it was declared at. The
// composite FK (Scope, ScopeKey, Endpoint) -> llm_endpoints(Scope, ScopeKey, Name) is declared in
// the migration and enforced by SQLite (PetBoxDb turns foreign_keys ON): a route CANNOT reference
// an endpoint from another level.
[Table("llm_routes")]
public sealed record LlmRouteRow
{
	[Column, PrimaryKey, NotNull] public string Id { get; init; } = string.Empty;

	[Column, NotNull] public string Scope { get; init; } = string.Empty;
	[Column, NotNull] public string ScopeKey { get; init; } = string.Empty;

	[Column, NotNull] public string Capability { get; init; } = string.Empty;
	[Column, NotNull] public string Endpoint { get; init; } = string.Empty;
	[Column, NotNull] public string Model { get; init; } = string.Empty;
	[Column, NotNull] public int Priority { get; init; }
	[Column, Nullable] public string? Tier { get; init; }
	[Column, Nullable] public string? Thinking { get; init; }

	[Column, NotNull] public DateTime UpdatedAt { get; init; }
	[Column, Nullable] public long? UpdatedBy { get; init; }
}
