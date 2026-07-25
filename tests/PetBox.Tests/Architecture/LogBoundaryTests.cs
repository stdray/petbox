using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Guards for the Log module's two boundaries — the MCP path and the presentation path.
//
// MCP: the log_query tool must reach a log only through ILogQueryService (the shared KQL
// execution path, also used by the REST log endpoint) — not by opening the log context itself.
//
// Presentation: a Razor page must reach a log only through ILogService, which returns
// materialized results and never leaks a LogDb. Eight pages used to hold ILogStore and open log
// SQLite files inline, each with its own take on paging, root-span grouping and the missing-table
// case; that is the drift this rule exists to stop coming back.
//
// Still legitimately on ILogStore, and deliberately out of scope: LogCatalogTools (owns the log
// *catalog* — log_create/list/delete — the same way RelationTools owns the relation store) and
// OtlpEndpoints (ingestion writes, which are not presentation).
public sealed class LogBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.LogTools).Assembly;

	// The CONNECTION doors only. Row records (LogEntryRecord, SpanRecord) and the naming helpers
	// (LogNames, DefaultLogSelector) share the Data namespace but hold no connection, so a page
	// may still name them — banning the whole namespace would forbid rendering a log at all.
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

	[Fact]
	public void WebPages_DoNotTouch_LogStoreOrContext()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespaceStartingWith("PetBox.Web.Pages")
			.Should().NotHaveDependencyOnAny(LogDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"a Razor page must go through ILogService, not open a LogDb itself; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
