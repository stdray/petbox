using System.ComponentModel;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;
using PetBox.Data.Contract;
using PetBox.Data.Schema;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP tools for the Data module's *operational* surface — the SQL/migration ops:
// data_schema_apply / data_query / data_exec. The DataDb lifecycle (db_create/list/
// delete/describe) lives in DataDbTools (kept separate so this type stays free of a
// raw Microsoft.Data.Sqlite dependency — a NetArchTest enforces that).
//
// query/exec delegate to the shared IDataSqlService — the same execution path the
// REST /api/data/* endpoints use — so the PRAGMA deny-list, parameter binding and
// existence check live in one place (a NetArchTest keeps these tools off the raw
// connection). schema_apply uses SchemaRunner (DbUp) directly. Tools throw on a failed
// Assert* (or a denied PRAGMA / SQL error); McpErrorEnvelopeFilter renders the {error} body.
[McpServerToolType]
public static class DataTools
{
	[McpServerTool(Name = "data_schema_apply", Title = "Apply schema migration", Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(DataSchemaApplyResult))]
	[Description("Applies a named SQL migration via DbUp + hash-based idempotency. Re-applying with same name+sql is a no-op; same name with different sql is a 409-style conflict. Requires data:schema scope.")]
	public static async Task<DataSchemaApplyResult> SchemaApplyAsync(
		IHttpContextAccessor http,
		PetBoxDb db,
		IDataDbFactory factory,
		SchemaRunner runner,
		string projectKey,
		string dbName,
		[Description("Migration script name. Used as journal key — same name = same migration.")] string name,
		[Description("SQL to apply. Multi-statement OK; PRAGMA statements may not parse with the SQLite dialect parser.")] string sql,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, ApiKeyScopes.DataSchema);

		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (row is null) throw new InvalidOperationException("DataDb not found");

		var cs = factory.GetConnectionString(projectKey, dbName);
		var result = runner.Apply(cs, name, sql);
		return new DataSchemaApplyResult(result.Kind.ToString(), result.Hash, result.ExistingHash, result.Error);
	}

	[McpServerTool(Name = "data_query", Title = "Run SQL query", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DataQueryResult))]
	[Description("Executes a parameterized SELECT and returns rows as a JSON array. Requires data:read scope.")]
	public static async Task<DataQueryResult> QueryAsync(
		IHttpContextAccessor http,
		IDataSqlService dataSql,
		string projectKey,
		string dbName,
		string sql,
		[Description("Optional parameter list as a JSON array of { name, value }. Pet builds via linq2db's ToSqlQuery().Parameters.")] JsonElement? @params = null,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, ApiKeyScopes.DataRead);
		var rows = await dataSql.QueryAsync(projectKey, dbName, sql, ParseArgs(@params), TimeoutSeconds, ct);
		return new DataQueryResult(rows);
	}

	[McpServerTool(Name = "data_exec", Title = "Run SQL exec (INSERT/UPDATE/DELETE/DDL)", UseStructuredContent = true, OutputSchemaType = typeof(DataExecResult))]
	[Description("Executes a non-query statement. Returns affected row count. PRAGMA writable_schema / temp_store_directory / data_store_directory / trusted_schema are denied. SQLITE_FULL surfaces as a quota error. Requires data:write scope.")]
	public static async Task<DataExecResult> ExecAsync(
		IHttpContextAccessor http,
		IDataSqlService dataSql,
		string projectKey,
		string dbName,
		string sql,
		JsonElement? @params = null,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
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

	static void AssertProject(IHttpContextAccessor accessor, string projectKey)
	{
		var ctx = accessor.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
			throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{projectKey}'");
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
