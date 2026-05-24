namespace YobaBox.Core.Models;

public sealed record User
{
	public long Id { get; init; }
	public string Username { get; init; } = string.Empty;
	public string PasswordHash { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }
}
