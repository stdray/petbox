using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Same shift-left guard as TasksBoundaryTests, for the Memory module: the MCP tools
// and the store page must reach Memory only through IMemoryService — never the store
// or DB context directly.
public sealed class MemoryBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.MemoryTools).Assembly;

	static readonly string[] MemoryDoors =
	[
		"PetBox.Memory.Data.IMemoryStore",
		"PetBox.Memory.Data.MemoryStore",
		"PetBox.Memory.Data.MemoryDb",
	];

	[Fact]
	public void WebMcpTools_DoNotTouch_MemoryStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Mcp")
			.Should().NotHaveDependencyOnAny(MemoryDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"MCP tools must reach Memory only through IMemoryService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	[Fact]
	public void WebPages_DoNotTouch_MemoryStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Pages")
			.Should().NotHaveDependencyOnAny(MemoryDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"Razor pages must reach Memory only through IMemoryService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
