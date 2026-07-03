using System.Data.Common;
using LinqToDB.Interceptors;
using Microsoft.Data.Sqlite;

namespace PetBox.Log.Core.Query;

// Registers the KQL scalar functions that SQLite has no built-in for (token `has`/`has_cs`, regex
// `matches regex`/`extract`) on every SQLite connection linq2db opens for a LogDb. Microsoft.Data.
// Sqlite scalar functions are per-connection, so we (re)register on ConnectionOpened. The function
// bodies are the same C# methods the in-memory path calls (KqlSqlExpressions), so a `where` running
// as SQL and the same predicate running in-memory are guaranteed to agree.
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
		c.CreateFunction<string?, string, bool>("kql_has", KqlSqlExpressions.Has, isDeterministic: true);
		c.CreateFunction<string?, string, bool>("kql_has_cs", KqlSqlExpressions.HasCs, isDeterministic: true);
		c.CreateFunction<string?, string, bool>("kql_matches_regex", KqlSqlExpressions.MatchesRegex, isDeterministic: true);
		c.CreateFunction<string?, long, string?, string>("kql_extract", KqlSqlExpressions.Extract, isDeterministic: true);
	}
}
