using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace PetBox.Core.Auth;

// spec apikey-last-used — the stamping half. Sits right after UseAuthentication and records, IN
// MEMORY, that this key just authenticated. Nothing here touches SQLite: the hot auth path already
// costs one indexed read (~53µs) and a per-request UPDATE would both double the write load and
// serialize callers behind the same row. KeyStatFlusher does the persisting, batched.
//
// A request with no key header short-circuits before any work: the cookie-authenticated Razor
// pages never pay for this. For a request that DOES carry a key, AuthenticateAsync is the SAME
// call the authorization middleware makes a moment later, and its result is cached per request by
// the handler — so this middleware adds a dictionary write, not a second key lookup.
public sealed class KeyUsageStampMiddleware(RequestDelegate next, IKeyStatService stats)
{
	public async Task InvokeAsync(HttpContext context)
	{
		var key = ApiKeyAuthenticationHandler.ExtractKey(context.Request);
		if (!string.IsNullOrEmpty(key))
		{
			// Only a SUCCESSFUL authentication is a use: a bogus or expired key must not be able to
			// keep a row looking alive (that would defeat the "is this key still used?" question the
			// column exists to answer).
			var result = await context.AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName);
			if (result.Succeeded)
				stats.Stamp(key);
		}

		await next(context);
	}
}
