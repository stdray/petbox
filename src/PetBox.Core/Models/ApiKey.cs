using LinqToDB.Mapping;

namespace PetBox.Core.Models;

[Table("ApiKeys")]
public sealed record ApiKey
{
	[PrimaryKey]
	public string Key { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public string Scopes { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }
	// Optional expiry. NULL = never expires (the default for normal keys). Set for temporary
	// agent/onboarding keys; the auth handler rejects the key once UtcNow passes this instant.
	public DateTime? ExpiresAt { get; init; }
}
