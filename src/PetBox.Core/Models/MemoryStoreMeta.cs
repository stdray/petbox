using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// Metadata row for a single named memory store. PK is (ProjectKey, Name). Entries
// live in `data/memory/{ProjectKey}/{Name}.db` (temporal table). v1 is
// project-scoped only; global/workspace scopes are a documented future extension.
// Mirrors LogMeta — explicit creation, no auto-vivify.
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
}
