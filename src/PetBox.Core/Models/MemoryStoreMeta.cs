using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// Metadata row for a single named memory store. PK is (ProjectKey, Name). Entries
// live in `data/memory/{ProjectKey}/{Name}.db` (temporal table). v1 is
// project-scoped only; global/workspace scopes are a documented future extension.
// Creation is EXPLICIT at the agent MCP layer (memory_store_create; a cold memory_upsert /
// memory_remember to an unknown store is rejected — spec agent-namespace-provisioning), but the
// service door still auto-vivifies on first write for background jobs and the reserved system
// stores (canon/notes/autocaptured/session-digests/ops).
[Table("MemoryStores")]
public sealed record MemoryStoreMeta
{
	[Column, PrimaryKey, NotNull]
	public string ProjectKey { get; init; } = string.Empty;

	[Column, PrimaryKey, NotNull]
	public string Name { get; init; } = string.Empty;

	[Column, Nullable]
	public string? Description { get; init; }

	[Column, NotNull]
	public DateTime CreatedAt { get; init; }

	[Column, NotNull]
	public DateTime UpdatedAt { get; init; }

	// System stores are machine plumbing (e.g. session-digests), not user knowledge: they
	// are excluded from the default memory_search sweep and set apart in the UI. An explicit
	// store: still reaches them (spec: memoverhaul store taxonomy).
	[Column, NotNull]
	public bool IsSystem { get; init; }
}
