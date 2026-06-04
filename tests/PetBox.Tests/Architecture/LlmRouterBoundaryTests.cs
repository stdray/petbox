using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Consumer-decoupling boundary (spec llm-consumer-decoupling): the MCP adapters must reach
// the router only through PetBox.LlmRouter.Contract — never the impl (routing/http/registry).
// Swapping the provider is then a DI change, not a change in any caller.
public sealed class LlmRouterBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.LlmRouterTools).Assembly;

	static readonly string[] ImplNamespaces =
	[
		"PetBox.LlmRouter.Routing",
		"PetBox.LlmRouter.Http",
		"PetBox.LlmRouter.Registry",
	];

	[Fact]
	public void WebMcpTools_DoNotDependOn_RouterImplementation()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Mcp")
			.Should().NotHaveDependencyOnAny(ImplNamespaces)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"MCP tools must reach the LLM router only through PetBox.LlmRouter.Contract; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	[Fact]
	public void WebPages_DoNotDependOn_RouterImplementation()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Pages")
			.Should().NotHaveDependencyOnAny(ImplNamespaces)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"Razor pages must reach the LLM router only through PetBox.LlmRouter.Contract; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
