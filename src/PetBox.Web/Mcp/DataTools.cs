using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Data.Contract;
using PetBox.Data.Schema;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP tools for the Data module's *operational* surface — the SQL/migration ops:
// data_schema_apply / data_query / data_exec. The DataDb lifecycle (db_create/list/
// delete/describe) lives in DataDbTools (kept separate so this type stays free of a
// raw Microsoft.Data.Sqlite dependency — a NetArchTest enforces that).
//
// All three delegate to the shared IDataSqlService — the same execution path the
// REST /api/data/* endpoints use — so the PRAGMA deny-list, parameter binding, the
// existence check and the quota'd connection (PRAGMA max_page_count is per-connection)
// live in one place, inside the Data module. This type never opens a connection itself;
// a NetArchTest keeps it off Microsoft.Data.Sqlite entirely. Tools throw on a failed
// Assert* (or a denied PRAGMA / SQL error); McpErrorEnvelopeFilter renders the {error} body.
// TENANT DECLARATION (spec authz-scope-declaration): the `projectKey` ARGUMENT, all three verbs —
// and this is the family where the ORDER the PEP restores matters most. data_query / data_exec /
// data_schema_apply hand raw SQL to a project's own database file; a check that ran inside the body
// was a check that ran after the call had already been bound. It now runs before the tool is entered
// at all. Same decision, same ProjectScope.EvaluateAsync, same sandbox containment.
[McpServerToolType]
[TenantFrom(TenantSource.Argument, "projectKey")]
public static class DataTools
{
	[McpServerTool(Name = "data_schema_apply", Title = "Apply schema migration", Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(DataSchemaApplyResult))]
	[Description("Applies a named SQL migration via DbUp + hash-based idempotency. Re-applying with same name+sql is a no-op (kind: 'AlreadyApplied'). Same name with different sql, or a SQL/DbUp failure, is a REFUSAL through the standard error envelope (not a field of a successful response) — the Conflict error names both the existing and the provided hash. Requires data:schema scope.")]
	public static async Task<DataSchemaApplyResult> SchemaApplyAsync(
		IHttpContextAccessor http,
		IDataSqlService dataSql,
		string projectKey,
		string dbName,
		[Description("Migration script name. Used as journal key — same name = same migration.")] string migrationName,
		[Description("SQL to apply. Multi-statement OK; PRAGMA statements may not parse with the SQLite dialect parser.")] string sql,
		CancellationToken ct = default)
	{
		AssertScope(http, ApiKeyScopes.DataSchema);

		var result = await dataSql.ApplySchemaAsync(projectKey, dbName, migrationName, sql, ct);
		// data_schema_apply used to be the one tool on the surface with its own error channel
		// around McpErrorEnvelopeFilter: Failed/Conflict rode home as FIELDS of a successful
		// response (kind:'Failed'+error, kind:'Conflict'+existingHash), invisible to anything that
		// only checked isError. AlreadyApplied is the one kind that stays a soft success — a no-op
		// re-apply is not a refusal. Applied/AlreadyApplied both report the same shape (kind + the
		// hash that is now on file); Conflict and Failed throw instead, through the central envelope,
		// and Conflict's message carries BOTH hashes (error text is a product surface here — a
		// caller deciding whether to bump migrationName needs to see what it collided with).
		return result.Kind switch
		{
			SchemaApplyKind.Applied or SchemaApplyKind.AlreadyApplied =>
				new DataSchemaApplyResult(result.Kind.ToString(), result.Hash),
			SchemaApplyKind.Conflict => throw new InvalidOperationException(
				$"data_schema_apply: migration '{migrationName}' was already applied with different sql — " +
				$"existingHash '{result.ExistingHash}', providedHash '{result.Hash}'. Re-apply with the " +
				"SAME sql to no-op, or pick a new migrationName for the changed script."),
			SchemaApplyKind.Failed => throw new ArgumentException(
				$"data_schema_apply: migration '{migrationName}' failed — {result.Error}"),
			_ => throw new InvalidOperationException(
				$"data_schema_apply: unexpected result kind '{result.Kind}'"),
		};
	}

	[McpServerTool(Name = "data_query", Title = "Run SQL query", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DataQueryResult))]
	[Description("Executes a parameterized SELECT and returns rows as a JSON array. Requires data:read scope.")]
	public static async Task<DataQueryResult> QueryAsync(
		IHttpContextAccessor http,
		IDataSqlService dataSql,
		string projectKey,
		string dbName,
		string sql,
		[McpJsonShape("array", "null")]
		[Description("Optional parameter list as a JSON array of { name, value }. Pet builds via linq2db's ToSqlQuery().Parameters.")] JsonElement? @params = null,
		CancellationToken ct = default)
	{
		AssertScope(http, ApiKeyScopes.DataRead);
		var rows = await dataSql.QueryAsync(projectKey, dbName, sql, ParseArgs(@params), TimeoutSeconds, ct);
		return new DataQueryResult(rows);
	}

	[McpServerTool(Name = "data_exec", Title = "Run SQL exec (INSERT/UPDATE/DELETE/DDL)", UseStructuredContent = true, OutputSchemaType = typeof(DataExecResult))]
	[Description("Executes a non-query statement. Returns affected row count. PRAGMA writable_schema / temp_store_directory / data_store_directory / trusted_schema are denied, and so is max_page_count — it IS the disk quota, so raising it would lift your own cap. Writing past the quota surfaces SQLITE_FULL as a quota error. Requires data:write scope.")]
	public static async Task<DataExecResult> ExecAsync(
		IHttpContextAccessor http,
		IDataSqlService dataSql,
		string projectKey,
		string dbName,
		string sql,
		[McpJsonShape("array", "null")]
		JsonElement? @params = null,
		CancellationToken ct = default)
	{
		AssertScope(http, ApiKeyScopes.DataWrite);
		var affected = await dataSql.ExecAsync(projectKey, dbName, sql, ParseArgs(@params), TimeoutSeconds, ct);
		return new DataExecResult(affected);
	}

	// --- Helpers ---------------------------------------------------------

	const int TimeoutSeconds = 30;

	static List<SqlArg> ParseArgs(JsonElement? @params)
	{
		if (@params is null || @params.Value.ValueKind != JsonValueKind.Array) return [];
		var list = new List<SqlArg>();
		foreach (var el in @params.Value.EnumerateArray())
		{
			if (el.ValueKind != JsonValueKind.Object) continue;
			if (!el.TryGetProperty("name", out var nameEl)) continue;
			var name = nameEl.GetString();
			if (string.IsNullOrEmpty(name)) continue;
			var value = el.TryGetProperty("value", out var v) ? (JsonElement?)v : null;
			list.Add(SqlArg.FromJson(name, value));
		}
		return list;
	}

	static void AssertScope(IHttpContextAccessor accessor, string required)
	{
		var ctx = accessor.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		var parts = scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!parts.Contains(required, StringComparer.Ordinal))
			throw new UnauthorizedAccessException($"ApiKey lacks required scope '{required}'");
	}
}
