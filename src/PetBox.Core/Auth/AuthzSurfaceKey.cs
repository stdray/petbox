using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

namespace PetBox.Core.Auth;

// THE IDENTITY of an externally reachable surface — the string the allowlist is keyed by, and the
// string both readers of that allowlist compute.
//
// It lives in PRODUCT code rather than in the ratchet test because the work card requires ONE list
// to drive both the ratchet and the enforcement point ("Один список управляет и тестом, и
// энфорсом — разойтись не могут"). A shared list is worth nothing if the two sides derive the KEY
// differently: the ratchet would count a surface as allowlisted while the PEP computed a different
// key for it, saw no entry, and refused it. So the key format is here, computed once, and both
// sides call it.
//
// THE THREE PREFIXES also keep the three planes from colliding: a route and an MCP tool could
// otherwise share a name, and crossing one out of the allowlist would exempt both.
public static class AuthzSurfaceKey
{
	public const string RestPrefix = "rest:";
	public const string PagePrefix = "page:";
	public const string McpPrefix = "mcp:";

	// The key of a live ENDPOINT — what the endpoint-plane PEP has in hand, and what the ratchet's
	// endpoint sweep produces. `null` = "not a surface this inventory addresses", which is exactly
	// two things and neither of them is a hole:
	//   * a non-route endpoint (nothing in this tree maps one; the ratchet's sweep skips them too, so
	//     the two sides agree);
	//   * the /mcp transport, whose ~100 tools are surfaces in their own right and are enforced by
	//     the MCP filter instead. Excluded BY CONSTRUCTION, never by an allowlist line — an
	//     allowlist entry would claim it is debt somebody should pay off, and it is not.
	public static string? OfEndpoint(Endpoint? endpoint)
	{
		if (endpoint is not RouteEndpoint route) return null;
		if (IsMcpTransport(route)) return null;

		// A Razor page is ONE surface even when several endpoints reach it: the declaration carrier is
		// the PageModel CLASS, so every endpoint of a page carries the SAME declaration and must map to
		// the SAME key. That is what makes a per-ENDPOINT enforcement point agree with a per-PAGE
		// inventory — see AuthzDeclarationRatchetTests.EveryEndpointOfAPage_CarriesTheSameDeclaration.
		if (route.Metadata.GetMetadata<PageActionDescriptor>() is { } page)
			return PagePrefix + page.ViewEnginePath;

		return RestPrefix + RestId(
			route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods, route.RoutePattern.RawText);
	}

	// "METHOD[|METHOD] /route/{pattern}" — the identity a caller actually addresses. Methods are
	// sorted so the id does not depend on the order MapMethods was handed them.
	public static string RestId(IReadOnlyList<string>? httpMethods, string? routePattern)
	{
		var verb = httpMethods is null or { Count: 0 }
			? "ANY"
			: string.Join("|", httpMethods.Order(StringComparer.Ordinal));
		var pattern = routePattern ?? "";
		return $"{verb} {(pattern.StartsWith('/') ? pattern : "/" + pattern)}";
	}

	// /mcp is a TRANSPORT, not a tenant surface (see OfEndpoint).
	public static bool IsMcpTransport(RouteEndpoint endpoint)
	{
		var pattern = endpoint.RoutePattern.RawText ?? "";
		return pattern is "/mcp" or "mcp"
			|| pattern.StartsWith("/mcp/", StringComparison.Ordinal)
			|| pattern.StartsWith("mcp/", StringComparison.Ordinal);
	}
}
