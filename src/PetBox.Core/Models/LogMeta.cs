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

	// spec log-retention-cascade: per-log retention override, in days. NULL (the default for
	// every log, old and new) means "no override" — RetentionService falls back to the
	// project → workspace → system cascade exactly as before this column existed. A log with a
	// value here is swept by that window regardless of what the cascade would otherwise say.
	[Column, Nullable]
	public int? RetentionDays { get; init; }

	[Column, NotNull]
	public DateTime CreatedAt { get; init; }

	[Column, NotNull]
	public DateTime UpdatedAt { get; init; }
}
