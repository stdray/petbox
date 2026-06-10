using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Guard for the Log query convergence: the log.query MCP tool must reach a log only
// through ILogQueryService (the shared KQL execution path, also used by the REST log
// endpoint) — not by opening the log context itself. So LogTools must not depend on
// ILogStore / LogDb. (LogCatalogTools legitimately uses ILogStore for the log *catalog*
// — log.create/list/delete — the same way RelationTools owns the relation store; and the
// read-only Logs browse pages + OTLP ingestion still use the store directly. Those are
// separate, lower-risk concerns, so this rule targets LogTools specifically.)
public sealed class LogBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.LogTools).Assembly;

	static readonly string[] LogDoors =
	[
		"PetBox.Log.Core.Data.ILogStore",
		"PetBox.Log.Core.Data.LogDb",
	];

	[Fact]
	public void LogTools_DoesNotTouch_LogStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().HaveName("LogTools")
			.Should().NotHaveDependencyOnAny(LogDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"the log.query tool must go through ILogQueryService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
