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

	// The claim carrying ApiKey.DefaultProjectKey — the project a cross-project ("*") key falls
	// back to when a tool's optional projectKey is omitted. Present only when the key has one.
	public const string DefaultProjectClaim = "project_default";

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var apiKey = Request.Headers[ApiKeyHeader].FirstOrDefault()
			?? Request.Headers[LegacyApiKeyHeader].FirstOrDefault()
			?? FromAuthorization(Request.Headers.Authorization.FirstOrDefault());
		if (string.IsNullOrEmpty(apiKey))
			return Task.FromResult(AuthenticateResult.NoResult());

		var lookup = Context.RequestServices.GetRequiredService<IApiKeyLookup>();
		var key = lookup.FindByKey(apiKey);

		if (key is null)
			return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));

		// Temporary agent/onboarding keys carry an expiry; reject once it passes.
		if (key.ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow)
			return Task.FromResult(AuthenticateResult.Fail("API key expired"));

		// `project_default` is emitted ONLY when the key carries one: an absent claim means
		// "no default", so the wildcard-key behavior is unchanged for every existing key.
		var claims = new List<Claim>
		{
			new("project", key.ProjectKey),
			new("scopes", key.Scopes),
		};
		if (!string.IsNullOrWhiteSpace(key.DefaultProjectKey))
			claims.Add(new Claim(DefaultProjectClaim, key.DefaultProjectKey.Trim()));

		var identity = new ClaimsIdentity(claims, SchemeName);
		var principal = new ClaimsPrincipal(identity);
		var ticket = new AuthenticationTicket(principal, SchemeName);

		return Task.FromResult(AuthenticateResult.Success(ticket));
	}

	// Also accept `Authorization: Token <key>` / `Authorization: Bearer <key>` — the form
	// the mem0 Claude Code plugin and many SDKs send — so they authenticate against PetBox
	// unchanged (the token IS the PetBox API key). X-Api-Key still takes precedence.
	static string? FromAuthorization(string? header)
	{
		if (string.IsNullOrWhiteSpace(header)) return null;
		var sp = header.IndexOf(' ');
		if (sp <= 0) return null;
		var scheme = header[..sp];
		var token = header[(sp + 1)..].Trim();
		return token.Length > 0
			&& (scheme.Equals("Token", StringComparison.OrdinalIgnoreCase)
				|| scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
			? token
			: null;
	}
}
