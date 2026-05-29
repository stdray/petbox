using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PetBox.Core.Auth;

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
	public const string SchemeName = "ApiKey";

	public ApiKeyAuthenticationHandler(
		IOptionsMonitor<AuthenticationSchemeOptions> options,
		ILoggerFactory logger,
		UrlEncoder encoder)
		: base(options, logger, encoder) { }

	// Legacy yobaconf clients send the key as X-YobaConf-ApiKey; native petbox uses X-Api-Key.
	// Both are accepted so the published config clients work against /v1/conf unchanged.
	public const string ApiKeyHeader = "X-Api-Key";
	public const string LegacyApiKeyHeader = "X-YobaConf-ApiKey";

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var apiKey = Request.Headers[ApiKeyHeader].FirstOrDefault()
			?? Request.Headers[LegacyApiKeyHeader].FirstOrDefault();
		if (string.IsNullOrEmpty(apiKey))
			return Task.FromResult(AuthenticateResult.NoResult());

		var lookup = Context.RequestServices.GetRequiredService<IApiKeyLookup>();
		var key = lookup.FindByKey(apiKey);

		if (key is null)
			return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));

		// Temporary agent/onboarding keys carry an expiry; reject once it passes.
		if (key.ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow)
			return Task.FromResult(AuthenticateResult.Fail("API key expired"));

		var claims = new[]
		{
			new Claim("project", key.ProjectKey),
			new Claim("scopes", key.Scopes),
		};
		var identity = new ClaimsIdentity(claims, SchemeName);
		var principal = new ClaimsPrincipal(identity);
		var ticket = new AuthenticationTicket(principal, SchemeName);

		return Task.FromResult(AuthenticateResult.Success(ticket));
	}
}
