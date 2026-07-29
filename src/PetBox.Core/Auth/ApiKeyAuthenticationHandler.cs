using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
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

	// Native petbox uses X-Api-Key; legacy yobaconf clients send X-YobaConf-ApiKey; a stock Seq
	// client (Serilog.Sinks.Seq, Seq.Extensions.Logging, @datalust/winston-seq) sends X-Seq-ApiKey
	// and nothing else.
	public const string ApiKeyHeader = "X-Api-Key";
	public const string LegacyApiKeyHeader = "X-YobaConf-ApiKey";
	private const string SeqApiKeyHeader = "X-Seq-ApiKey";

	// WHICH HEADER CARRIES THE KEY IS A PARAMETER OF AUTHENTICATION, NOT A PROPERTY OF A ROUTE.
	//
	// This list is the whole reason the Seq ingest routes could stop being special. They used to be
	// `.AllowAnonymous()` and read X-Seq-ApiKey by hand, on the argument that "a stock Seq client
	// cannot send X-Api-Key" — but that is a limitation of the CLIENT, and it was being passed off as
	// a limitation of ours. The tenant decision point already answers on an api key AND on a session
	// cookie, so it does not care where the principal came from; the only real problem was that no
	// principal was ever created for a Seq request. So the fix belongs HERE, in authentication, and
	// the surfaces go back to declaring [TenantFrom(...)] like everything else.
	//
	// DATA, IN ONE PLACE, and deliberately not a branch on route: a per-route header rule is how the
	// exemption grows back. Every header below yields THE SAME claims off THE SAME key row — there is
	// no such thing as a "Seq key", only a PetBox api key presented in a Seq-shaped header.
	//
	// Order is precedence, and it only decides which header wins when a caller sends several.
	private static readonly IReadOnlyList<string> KeyHeaders = [ApiKeyHeader, LegacyApiKeyHeader, SeqApiKeyHeader];

	// The claim carrying ApiKey.ProjectKey — the tenant this key is scoped to, or the cross-project
	// wildcard ProjectScope.AllProjects. Always emitted. Named here because this handler is what
	// emits it; TenantAuthorizer reads it off THIS identity rather than off the merged principal.
	public const string ProjectClaim = "project";

	// The claim carrying ApiKey.DefaultProjectKey — the project a cross-project ("*") key falls
	// back to when a tool's optional projectKey is omitted. Present only when the key has one.
	public const string DefaultProjectClaim = "project_default";

	// The claim carrying ApiKey.SandboxOnly (spec work/smoke-writes-into-real-projects). Present
	// (value "true") ONLY when the key is sandbox-only — an absent claim means "no containment
	// check", i.e. the old behavior, for every existing key. ProjectScope.AuthorizesAsync reads it.
	public const string SandboxOnlyClaim = "sandbox_only";

	// The ONE place that knows where a key may arrive from — shared with KeyUsageStampMiddleware and
	// with LogApi's Seq ingest (which compares the presented key against the configured self-log key
	// to pick a DESTINATION, never to authenticate), so the stamp is keyed by exactly the key this
	// handler authenticated. A second header-parsing implementation would drift and quietly stop
	// stamping the legacy/Seq/Authorization callers — which is precisely what the hand-rolled
	// `Headers["X-Seq-ApiKey"]` reads in LogApi were doing before they were deleted.
	//
	// `Authorization: Bearer|Token <key>` stays the LAST resort rather than a name in KeyHeaders: it
	// is not a bare header value but a scheme-prefixed one, so it needs FromAuthorization's parse and
	// cannot be looked up by name alongside the others. It is accepted (it already was — the mem0
	// Claude Code plugin and many SDKs send it), and it is listed here so the full set of ways to
	// present a key is still readable in one place.
	public static string? ExtractKey(HttpRequest request)
	{
		foreach (var header in KeyHeaders)
			if (request.Headers[header].FirstOrDefault() is { } value)
				return value;

		return FromAuthorization(request.Headers.Authorization.FirstOrDefault());
	}

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var apiKey = ExtractKey(Request);
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
		if (key.SandboxOnly)
			claims.Add(new Claim(SandboxOnlyClaim, "true"));

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
