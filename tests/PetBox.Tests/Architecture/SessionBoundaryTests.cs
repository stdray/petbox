using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Same shift-left guard for the Sessions module: the MCP tools and the REST Stop-hook
// endpoint must reach Sessions only through ISessionService — never the store or DB
// context directly. (Sessions has no Razor pages; the REST endpoint lives in
// PetBox.Web.Sessions, so the rule spans the whole Web assembly except DI wiring.)
public sealed class SessionBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.SessionTools).Assembly;

	static readonly string[] SessionDoors =
	[
		"PetBox.Sessions.Data.ISessionStore",
		"PetBox.Sessions.Data.SessionStore",
		"PetBox.Sessions.Data.SessionsDb",
	];

	[Fact]
	public void WebMcpTools_DoNotTouch_SessionStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Mcp")
			.Should().NotHaveDependencyOnAny(SessionDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"MCP tools must reach Sessions only through ISessionService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	[Fact]
	public void WebSessionApi_DoesNotTouch_SessionStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Sessions")
			.Should().NotHaveDependencyOnAny(SessionDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"the REST session endpoint must go through ISessionService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
