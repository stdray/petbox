using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// Metadata row for a single named log SQLite database. PK is (ProjectKey, Name).
// The actual events live in `data/logs/{ProjectKey}/{Name}.db`; this table tracks
// which logs exist. Mirrors DataDb (user-data); logs have no per-DB size quota.
[Table("Logs")]
public sealed record LogMeta
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
