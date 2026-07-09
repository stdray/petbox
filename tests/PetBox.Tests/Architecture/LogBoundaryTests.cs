using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Guard for the Log query convergence: the log_query MCP tool must reach a log only
// through ILogQueryService (the shared KQL execution path, also used by the REST log
// endpoint) — not by opening the log context itself. So LogTools must not depend on
// ILogStore / LogDb. (LogCatalogTools legitimately uses ILogStore for the log *catalog*
// — log_create/list/delete — the same way RelationTools owns the relation store; and the
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
			"the log_query tool must go through ILogQueryService; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	// Guard against Razor Pages reaching into Log's data layer directly.
	// Admin pages (ProjectLogsModel) are excluded — they legitimately manage the log
	// catalog (create/list/delete), the same way LogCatalogTools does in MCP.
	[Fact]
	public void WebPages_DoNotTouch_LogStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespace("PetBox.Web.Pages")
			.And().DoNotResideInNamespace("PetBox.Web.Pages.Admin")
			.Should().NotHaveDependencyOnAny(LogDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"Razor pages must not reach into Log data layer directly; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
