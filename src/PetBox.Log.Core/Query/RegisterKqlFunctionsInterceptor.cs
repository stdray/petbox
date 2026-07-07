using System.Data.Common;
using LinqToDB.Interceptors;
using Microsoft.Data.Sqlite;

namespace PetBox.Log.Core.Query;

// Registers the KQL typed string→value conversion functions that SQLite's CAST cannot express
// faithfully (tolong/todouble/tobool/todatetime — CAST('abc' AS INTEGER) is 0, not NULL) on every
// SQLite connection linq2db opens for a LogDb. Microsoft.Data.Sqlite scalar functions are
// per-connection, so we (re)register on ConnectionOpened. The function bodies are the same C# methods
// the in-memory path calls (KqlSqlExpressions), so a `where` running as SQL and the same predicate
// running in-memory agree. (The regex surfaces `matches regex`/`extract`/`has`/`has_cs` no longer live
// here: they map to native per-dialect regexp_* SQL via [Sql.Expression] — see KqlSqlExpressions.)
sealed class RegisterKqlFunctionsInterceptor : ConnectionInterceptor
{
	public static readonly RegisterKqlFunctionsInterceptor Instance = new();

	public override void ConnectionOpened(ConnectionEventData eventData, DbConnection connection)
	{
		Register(connection);
		base.ConnectionOpened(eventData, connection);
	}

	public override async Task ConnectionOpenedAsync(
		ConnectionEventData eventData, DbConnection connection, CancellationToken cancellationToken)
	{
		Register(connection);
		await base.ConnectionOpenedAsync(eventData, connection, cancellationToken).ConfigureAwait(false);
	}

	static void Register(DbConnection connection)
	{
		if (connection is not SqliteConnection c)
			return;
		// typed conversions: string → nullable value, NULL on malformed input (Kusto semantics)
		c.CreateFunction<string?, long?>("kql_tolong", KqlSqlExpressions.ParseLong, isDeterministic: true);
		c.CreateFunction<string?, double?>("kql_todouble", KqlSqlExpressions.ParseDouble, isDeterministic: true);
		c.CreateFunction<string?, bool?>("kql_tobool", KqlSqlExpressions.ParseBool, isDeterministic: true);
		c.CreateFunction<string?, long?>("kql_todatetime", KqlSqlExpressions.ParseDateTimeMs, isDeterministic: true);
	}
}
