using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// Guard for the Data tier convergence: the data_query / data_exec tools must not
// re-implement the raw-SQL loop (open a SqliteConnection, bind, read) — that
// execution path lives once, in IDataSqlService, shared with the REST /api/data/*
// endpoints. So DataTools must not depend on Microsoft.Data.Sqlite directly.
// (db_describe in DataDbTools still introspects schema over its own connection, and the
// read-only Data browse pages open theirs — converging those is separate work.)
public sealed class DataBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.DataTools).Assembly;

	[Fact]
	public void DataTools_DoNotOpen_RawSqliteConnections()
	{
		var result = Types.InAssembly(Web)
			.That().HaveName("DataTools")
			.Should().NotHaveDependencyOn("Microsoft.Data.Sqlite")
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"data_query/data_exec must run SQL through IDataSqlService, not a raw connection; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}
}
