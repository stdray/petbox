namespace YobaBox.Core.Models;

public sealed record ConfigBinding
{
	public long Id { get; init; }
	public string Path { get; init; } = string.Empty;
	public string Value { get; init; } = string.Empty;
	public string Tags { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }
	public DateTime UpdatedAt { get; init; }
}
