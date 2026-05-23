namespace YobaBox.Core.Models;

public sealed record ApiKey
{
	public string Key { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public string Scopes { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }
}
