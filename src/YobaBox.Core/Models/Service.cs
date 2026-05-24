using LinqToDB.Mapping;

namespace YobaBox.Core.Models;

[Table("Services")]
public sealed record Service
{
	[PrimaryKey]
	public string Key { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public HealthModel HealthModel { get; init; }
	public string? Url { get; init; }
	public string? Version { get; init; }
	public string? ShortSha { get; init; }
	public ServiceHealth Health { get; init; }
	public DateTime? CheckedAt { get; init; }
}
