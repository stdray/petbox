using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// whoami is a pure self-identification tool (no DB) — call it directly with a
// mocked HttpContext carrying the project/scopes claims the ApiKey handler sets.
public sealed class WhoAmIToolsTests
{
	static IHttpContextAccessor Http(string project, string scopes, string? host = null)
	{
		var claims = new List<Claim> { new("project", project), new("scopes", scopes) };
		// The claim TYPE comes from the handler that emits it, not from a literal here: a rename on one
		// side and not the other is exactly the class of miss this test exists to catch.
		if (host is not null) claims.Add(new Claim(ApiKeyAuthenticationHandler.HostClaim, host));
		var id = new ClaimsIdentity(claims, "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	// Serialize the way the MCP boundary does (camelCase policy), so a typed-record result
	// reads the same as the live JSON: WhoAmIResult.Project -> "project", Scopes -> "scopes".
	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, CamelCase);

	[Fact]
	public void WhoAmI_ReturnsProjectAndScopes()
	{
		var r = Json(WhoAmITools.WhoAmI(Http("kpvotes", "data:read, logs:query ,tasks:write")));
		r.GetProperty("project").GetString().Should().Be("kpvotes");
		r.GetProperty("scopes").EnumerateArray().Select(e => e.GetString())
			.Should().Equal("data:read", "logs:query", "tasks:write");
	}

	[Fact]
	public void WhoAmI_NoScopes_ReturnsEmptyScopes()
	{
		var r = Json(WhoAmITools.WhoAmI(Http("$system", "")));
		r.GetProperty("project").GetString().Should().Be("$system");
		r.GetProperty("scopes").GetArrayLength().Should().Be(0);
	}

	// apikey-principal-authz-cluster, finding 4. A NODE-AGENT key carries an EMPTY project claim since
	// M050 and identifies a MACHINE through the `host` claim instead. whoami read `project` and
	// `project_default` and knew nothing about `host`, so calling it with a node key answered
	// `{ project: "", scopes: [...] }` — indistinguishable from a broken or half-provisioned key, on the
	// one tool whose entire job is telling a caller who it is.
	[Fact]
	public void WhoAmI_NodeKey_ReportsTheHostItIsBoundTo()
	{
		var r = Json(WhoAmITools.WhoAmI(Http("", "agent:poll, agent:heartbeat", host: "local-pc")));
		r.GetProperty("host").GetString().Should().Be("local-pc",
			"a node key's identity IS the host — without it whoami reports an empty project and nothing else");
		r.GetProperty("project").GetString().Should().BeEmpty(
			"the empty project claim is not the bug; reporting it as the whole answer was");
	}

	// The other half: `host` must stay OFF an ordinary project key's answer. Its ABSENCE is the signal
	// that the caller is not a node (the handler emits the claim only for a host-bound key), so a
	// field that always appeared — as null, or worse as "" — would make that signal unreadable.
	[Fact]
	public void WhoAmI_ProjectKey_OmitsHostEntirely()
	{
		var r = Json(WhoAmITools.WhoAmI(Http("kpvotes", "data:read")));
		var absentOrNull = !r.TryGetProperty("host", out var host) || host.ValueKind == JsonValueKind.Null;
		absentOrNull.Should().BeTrue(
			"an ordinary project key has no host claim, and nothing must appear in its place (the MCP "
			+ "boundary drops the null outright; either way there is no host to read)");
	}
}
