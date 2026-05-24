using LinqToDB.Mapping;

namespace YobaBox.Core.Models;

[Table("SavedQueries")]
public sealed record SavedQuery
{
	[Identity, PrimaryKey]
	public long Id { get; init; }

	public string Name { get; init; } = string.Empty;
	public string Kql { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }
	public DateTime UpdatedAt { get; init; }
}
