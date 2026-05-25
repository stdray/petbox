using LinqToDB.Mapping;

namespace YobaBox.Core.Models;

[Table("RetentionPolicies")]
public sealed record RetentionPolicy
{
	[Column, Identity, PrimaryKey]
	public long Id { get; init; }
	[Column, NotNull]
	public string ProjectKey { get; init; } = string.Empty;
	[Column]
	public int RetainDays { get; init; } = 7;
	[Column]
	public DateTime CreatedAt { get; init; }
	[Column]
	public DateTime UpdatedAt { get; init; }
}
