using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// Metadata row for a single user-data SQLite database. PK is (ProjectKey, Name).
[Table("DataDbs")]
public sealed record DataDb
{
	[Column, PrimaryKey, NotNull]
	public string ProjectKey { get; init; } = string.Empty;

	[Column, PrimaryKey, NotNull]
	public string Name { get; init; } = string.Empty;

	[Column, Nullable]
	public string? Description { get; init; }

	[Column, NotNull]
	public long MaxPageCount { get; init; }

	[Column, NotNull]
	public DateTime CreatedAt { get; init; }

	[Column, NotNull]
	public DateTime UpdatedAt { get; init; }
}
