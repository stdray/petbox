using System.Text.Json;

namespace PetBox.Data.Contract;

// The single execution path for raw user SQL against a project's DataDb. Both the
// MCP data_query/data_exec tools and the REST /api/data/{p}/{db}/query|exec endpoints
// used to re-implement this loop (open connection, bind params, read/exec) and had
// drifted (the REST path gained a PRAGMA deny-list, a configurable timeout and a
// SQLITE_FULL mapping the MCP path lacked). Routing both here keeps the security rules
// and the execution in one place; each adapter keeps its own auth, body limits and
// response shape. SqliteException is NOT caught here — it bubbles so each adapter maps
// it (REST: SQLITE_FULL -> 507, else 400).
public interface IDataSqlService
{
	// SELECT-style: returns rows as column-name -> value dictionaries.
	Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
		string projectKey, string dbName, string sql, IReadOnlyList<SqlArg> parameters, int timeoutSeconds, CancellationToken ct = default);

	// Non-query (INSERT/UPDATE/DELETE/DDL): returns affected row count. Applies the
	// PRAGMA deny-list (throws DeniedPragmaException).
	Task<int> ExecAsync(
		string projectKey, string dbName, string sql, IReadOnlyList<SqlArg> parameters, int timeoutSeconds, CancellationToken ct = default);
}

// A bound SQL parameter with an already-CLR value (null => DBNull at bind time).
public sealed record SqlArg(string Name, object? Value)
{
	// Shared JSON->CLR conversion used by both adapters (arrays/objects stored as JSON text).
	public static SqlArg FromJson(string name, JsonElement? value) => new(name, Convert(value));

	static object? Convert(JsonElement? je)
	{
		if (je is null) return null;
		var el = je.Value;
		return el.ValueKind switch
		{
			JsonValueKind.Null or JsonValueKind.Undefined => null,
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Number when el.TryGetInt64(out var l) => l,
			JsonValueKind.Number => el.GetDouble(),
			JsonValueKind.String => el.GetString(),
			_ => el.GetRawText(),
		};
	}
}

// The named DataDb does not exist for this project. Adapters map it (REST -> 404).
public sealed class DataDbNotFoundException(string projectKey, string dbName)
	: Exception($"DataDb '{dbName}' not found in project '{projectKey}'");

// A denied PRAGMA was submitted to exec. Adapters map it (REST -> 400 with the name).
public sealed class DeniedPragmaException(string pragmaName)
	: Exception($"PRAGMA {pragmaName} is not allowed")
{
	public string PragmaName { get; } = pragmaName;
}
