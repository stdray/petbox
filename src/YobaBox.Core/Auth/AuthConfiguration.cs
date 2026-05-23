namespace YobaBox.Core.Auth;

public sealed class AuthConfiguration
{
	public const string Section = "Auth";

	public string Mode { get; init; } = "local";
	public string? RemoteUrl { get; init; }
	public string? RemoteApiKey { get; init; }
	public string? AdminPasswordHash { get; init; }
	public string? AdminUsername { get; init; }
}
