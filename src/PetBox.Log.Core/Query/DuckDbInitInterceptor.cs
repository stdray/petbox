using System.Data.Common;
using LinqToDB.Interceptors;

namespace PetBox.Log.Core.Query;

// Pins the session time zone to UTC on every DuckDB connection linq2db opens for a DuckDb-backed LogDb.
// DuckDB has no per-connection loadable extension to bootstrap (its regexp_*/TRY_CAST/json_* are native),
// so unlike LoadSqleanRegexpInterceptor this only runs one pragma: the todatetime translation
// (KqlSqlExpressions.ParseDateTimeMs → epoch_ms(TRY_CAST(... AS TIMESTAMPTZ))) reads an unspecified-offset
// string against the SESSION TimeZone, which MUST be UTC to honor the SQLite/AssumeUtc contract.
//
// No DuckDB.NET dependency: the pragma runs through a plain DbConnection command (mirrors
// LoadSqleanRegexpInterceptor's per-connection ConnectionOpened hook). This interceptor is attached ONLY
// to the DuckDb options (LogDb.CreateDuckDbOptions), so every connection it sees is a DuckDB connection.
sealed class DuckDbInitInterceptor : ConnectionInterceptor
{
	public static readonly DuckDbInitInterceptor Instance = new();

	public override void ConnectionOpened(ConnectionEventData eventData, DbConnection connection)
	{
		SetUtc(connection);
		base.ConnectionOpened(eventData, connection);
	}

	public override async Task ConnectionOpenedAsync(
		ConnectionEventData eventData, DbConnection connection, CancellationToken cancellationToken)
	{
		SetUtc(connection);
		await base.ConnectionOpenedAsync(eventData, connection, cancellationToken).ConfigureAwait(false);
	}

	static void SetUtc(DbConnection connection)
	{
		using var cmd = connection.CreateCommand();
		cmd.CommandText = "SET TimeZone='UTC'";
		cmd.ExecuteNonQuery();
	}
}
