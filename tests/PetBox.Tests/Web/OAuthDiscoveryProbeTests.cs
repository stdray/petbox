using System.Net;
using System.Text.Json;

namespace PetBox.Tests.Web;

// Regression guard for bug mcp-oauth-discovery-html-404.
//
// MCP SDK clients (Claude Code) register an OAuth auth provider for every http MCP server and, on a
// 401 (e.g. a reconnect after a server restart), probe the RFC 9728 / RFC 8414 well-known discovery
// paths. PetBox authenticates /mcp with an API key, not OAuth, and publishes no metadata — but these
// probes MUST answer with a clean JSON 404, never the SPA's HTML error page. An HTML body makes the
// client's `JSON.parse` throw ("Invalid OAuth error response: Unrecognized token '<'") and abort the
// whole reconnect (a dead /mcp). Reuses the anonymous LogPipelineFixture client — discovery is
// pre-auth, so no key is sent.
[Collection(LogPipelineCollectionDef.Name)]
public sealed class OAuthDiscoveryProbeTests
{
	readonly HttpClient _client;

	public OAuthDiscoveryProbeTests(LogPipelineFixture fx) => _client = fx.Client;

	public static TheoryData<string> DiscoveryPaths() => new()
	{
		"/.well-known/oauth-protected-resource",
		"/.well-known/oauth-authorization-server",
		"/.well-known/openid-configuration",
		// spec appends the resource path — must hit the catch-all, not the SPA.
		"/.well-known/oauth-protected-resource/mcp",
		"/.well-known/oauth-authorization-server/mcp",
	};

	[Theory]
	[MemberData(nameof(DiscoveryPaths))]
	public async Task Discovery_probe_returns_json_404_not_html(string path)
	{
		using var resp = await _client.GetAsync(path);

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
		// The crash was HTML being JSON-parsed. Assert the client receives parseable JSON.
		Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
		var body = await resp.Content.ReadAsStringAsync();
		Assert.StartsWith("{", body.TrimStart());
		Assert.DoesNotContain("<!DOCTYPE", body, StringComparison.OrdinalIgnoreCase);
		using var doc = JsonDocument.Parse(body); // must not throw
		Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetString());
	}
}
