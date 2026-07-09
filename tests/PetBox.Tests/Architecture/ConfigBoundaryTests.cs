using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Same shift-left guard as the other module boundary tests, for Config: the MCP tools
// (ConfigTools) are the sanctioned adapter that may use IConfigDbFactory, while Razor
// pages and REST APIs must not reach into Config's data layer directly.
public sealed class ConfigBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.LogTools).Assembly;

	static readonly string[] ConfigDoors =
	[
		"PetBox.Config.Data.IConfigDbFactory",
		"PetBox.Config.Data.ConfigDb",
	];

	[Fact]
	public void WebPages_DoNotTouch_ConfigDbFactory()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Pages")
			.Should().NotHaveDependencyOnAny(ConfigDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"Razor pages must not reach into Config data layer directly; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	[Fact]
	public void WebDeployApi_DoesNotTouch_ConfigDbFactory()
	{
		var result = Types.InAssembly(Web)
			.That().HaveName("DeployApi")
			.Should().NotHaveDependencyOnAny(ConfigDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"DeployApi must not reach into Config data layer directly; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
