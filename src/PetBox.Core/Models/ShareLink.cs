using LinqToDB.Mapping;

namespace PetBox.Core.Models;

[Table("ShareLinks")]
public sealed record ShareLink
{
	[Column, PrimaryKey, NotNull]
	public string Id { get; init; } = string.Empty;
	[Column, NotNull]
	public string ProjectKey { get; init; } = string.Empty;
	[Column, NotNull]
	public string Kql { get; init; } = string.Empty;
	[Column]
	public DateTime CreatedAt { get; init; }
	[Column]
	public DateTime ExpiresAt { get; init; }
	[Column, NotNull]
	public string SaltBase64 { get; init; } = string.Empty;
	[Column, NotNull]
	public string ColumnsJson { get; init; } = "[]";
	[Column, NotNull]
	public string ModesJson { get; init; } = "{}";
	[Column, NotNull]
	public string CreatedBy { get; init; } = "system";
}
