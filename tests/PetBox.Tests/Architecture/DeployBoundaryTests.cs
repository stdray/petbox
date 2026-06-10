using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Same shift-left guard as the other module boundary tests, for Deploy: the MCP tools
// (slice 5) and Razor pages must reach Deploy only through IDeployService — never the
// concrete service or the DeployDb context. DI wiring in Program is exempt (it lives
// outside the Mcp/Pages namespaces). Guards future slices; passes vacuously today.
public sealed class DeployBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.MemoryTools).Assembly;

	static readonly string[] DeployDoors =
	[
		"PetBox.Deploy.Services.DeployService",
		"PetBox.Deploy.Data.DeployDb",
	];

	[Fact]
	public void WebMcpTools_DoNotTouch_DeployServiceOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Mcp")
			.Should().NotHaveDependencyOnAny(DeployDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"MCP tools must reach Deploy only through IDeployService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	[Fact]
	public void WebPages_DoNotTouch_DeployServiceOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Pages")
			.Should().NotHaveDependencyOnAny(DeployDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"Razor pages must reach Deploy only through IDeployService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
