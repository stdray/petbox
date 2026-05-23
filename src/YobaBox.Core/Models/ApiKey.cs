using LinqToDB.Mapping;

namespace YobaBox.Core.Models;

[Table("ApiKeys")]
public sealed record ApiKey
{
	[PrimaryKey]
	public string Key { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public string Scopes { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }
}
