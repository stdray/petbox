using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// whoami is a pure self-identification tool (no DB) — call it directly with a
// mocked HttpContext carrying the project/scopes claims the ApiKey handler sets.
public sealed class WhoAmIToolsTests
{
	static IHttpContextAccessor Http(string project, string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", project), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

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
}
