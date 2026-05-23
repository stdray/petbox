using LinqToDB.Mapping;

namespace YobaBox.Core.Models;

[Table("Projects")]
public sealed record Project
{
	[PrimaryKey]
	public string Key { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;
}
