namespace PetBox.Core.Auth;

public sealed record AdminOptions
{
	public string Username { get; init; } = string.Empty;
	public string PasswordHash { get; init; } = string.Empty;
}
