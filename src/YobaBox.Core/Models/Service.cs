namespace YobaBox.Core.Models;

public sealed record Service
{
	public string Key { get; init; } = string.Empty;
	public string ProjectKey { get; init; } = string.Empty;
	public ServiceKind Kind { get; init; }
	public string? Url { get; init; }
	public string? Version { get; init; }
	public string? ShortSha { get; init; }
	public ServiceHealth Health { get; init; }
	public DateTime? CheckedAt { get; init; }
}
