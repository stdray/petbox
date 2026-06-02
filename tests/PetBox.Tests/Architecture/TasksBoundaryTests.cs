using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// "Shift-left" architecture guard for the Tasks service-layer refactor: the MCP tools
// and Razor pages must reach the Tasks module ONLY through ITasksService — never the
// board store/context directly. The compiler can't enforce this (TasksDb/GetContext stay
// public so white-box tests can open the context), so we encode the boundary as a test
// that fails the build the moment a Web type re-introduces a direct DB path — the exact
// divergence (UI/MCP each carving their own way into the DB) this refactor removes.
public sealed class TasksBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.TasksTools).Assembly;

	// The board catalog + per-board context: the path that UI and MCP used to reach by
	// divergent routes. No Web type may depend on these — go through ITasksService.
	static readonly string[] BoardDoors =
	[
		"PetBox.Tasks.Data.ITaskBoardStore",
		"PetBox.Tasks.Data.TaskBoardStore",
		"PetBox.Tasks.Data.TasksDb",
	];

	// The project-level relation store. RelationTools is its sanctioned adapter (a single
	// generic CRUD surface, no UI twin — a service interface there would be indirection
	// without isolation), so it is the one exception; nothing else may touch it.
	static readonly string[] RelationDoors =
	[
		"PetBox.Tasks.Data.IRelationStore",
		"PetBox.Tasks.Data.RelationStore",
	];

	// Domain types (PlanNode, TaskNodeId, the Contract DTOs, WorkflowCatalog) are
	// deliberately absent from both lists — they flow freely to adapters.

	[Fact]
	public void WebMcpTools_DoNotTouch_BoardStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Mcp")
			.Should().NotHaveDependencyOnAny(BoardDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"MCP tools must reach the task board only through ITasksService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	[Fact]
	public void WebMcpToolsExceptRelationTools_DoNotTouch_RelationStore()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Mcp")
			.And().DoNotHaveName("RelationTools")
			.Should().NotHaveDependencyOnAny(RelationDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"only RelationTools may use the relation store directly; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	[Fact]
	public void WebPages_DoNotTouch_TasksStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Pages")
			.Should().NotHaveDependencyOnAny([.. BoardDoors, .. RelationDoors])
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"Razor pages must reach the Tasks module only through ITasksService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
