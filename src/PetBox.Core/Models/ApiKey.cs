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
	// The project a CROSS-PROJECT key ("*" ProjectKey) falls back to when a tool's optional
	// projectKey is omitted. The wildcard claim AUTHORIZES every project but SUPPLIES none, so
	// without this a "*" key must repeat projectKey on every call. NULL = no default (the old
	// behavior: an omitted projectKey is an error). Meaningless on a project-scoped key — it
	// already defaults to its own claim — so apikey_create rejects the combination.
	public string? DefaultProjectKey { get; init; }
}
