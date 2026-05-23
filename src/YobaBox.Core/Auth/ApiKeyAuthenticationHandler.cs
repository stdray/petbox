using System.Security.Claims;
using System.Text.Encodings.Web;
using LinqToDB;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Core.Auth;

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
	public const string SchemeName = "ApiKey";

	public ApiKeyAuthenticationHandler(
		IOptionsMonitor<AuthenticationSchemeOptions> options,
		ILoggerFactory logger,
		UrlEncoder encoder)
		: base(options, logger, encoder) { }

	protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();
		if (string.IsNullOrEmpty(apiKey))
			return AuthenticateResult.NoResult();

		var db = Context.RequestServices.GetRequiredService<YobaBoxDb>();
		var key = await db.ApiKeys
			.FirstOrDefaultAsync((ApiKey k) => k.Key == apiKey);

		if (key is null)
			return AuthenticateResult.Fail("Invalid API key");

		var claims = new[]
		{
			new Claim("project", key.ProjectKey),
			new Claim("scopes", key.Scopes),
		};
		var identity = new ClaimsIdentity(claims, SchemeName);
		var principal = new ClaimsPrincipal(identity);
		var ticket = new AuthenticationTicket(principal, SchemeName);

		return AuthenticateResult.Success(ticket);
	}
}
