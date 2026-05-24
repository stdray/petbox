namespace YobaBox.Core.Models;

public sealed record Workspace
{
	public string Key { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }
}
