using System.ComponentModel;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;
using PetBox.Data.Schema;

namespace PetBox.Web.Mcp;

// MCP tools for the Data module's *operational* surface — the SQL/migration
// ops that don't fit generic CRUD: data.schema_apply / data.query / data.exec.
// DataDb lifecycle (list/create/delete/describe) moved to the generic
// entity.* tools (type "db") — see EntityTools.
//
// Each tool maps 1:1 onto a /api/data/* REST endpoint — same auth (X-Api-Key
// flows through the standard pipeline before reaching the tool), same scopes
// (data:read / data:write / data:schema), same project-claim cross-check. We do
// NOT proxy through the HTTP handlers; tools call the same underlying services
// (PetBoxDb, IDataDbFactory, SchemaRunner) that the REST handlers use.
[McpServerToolType]
public static class DataTools
{
	[McpServerTool(Name = "data.schema_apply", Title = "Apply schema migration", Idempotent = true)]
	[Description("Applies a named SQL migration via DbUp + hash-based idempotency. Re-applying with same name+sql is a no-op; same name with different sql is a 409-style conflict. Requires data:schema scope.")]
	public static async Task<object> SchemaApplyAsync(
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
		return new { kind = result.Kind.ToString(), hash = result.Hash, existingHash = result.ExistingHash, error = result.Error };
	}

	[McpServerTool(Name = "data.query", Title = "Run SQL query", ReadOnly = true)]
	[Description("Executes a parameterized SELECT and returns rows as a JSON array. Requires data:read scope.")]
	public static async Task<object> QueryAsync(
		IHttpContextAccessor http,
		PetBoxDb db,
		IDataDbFactory factory,
		string projectKey,
		string dbName,
		string sql,
		[Description("Optional parameter list as a JSON array of { name, value }. Pet builds via linq2db's ToSqlQuery().Parameters.")] JsonElement? @params = null,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, ApiKeyScopes.DataRead);

		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (row is null) throw new InvalidOperationException("DataDb not found");

		var cs = factory.GetConnectionString(projectKey, dbName);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync(ct);
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.CommandTimeout = 30;
		BindJsonParams(cmd, @params);

		var rows = new List<Dictionary<string, object?>>();
		await using var reader = await cmd.ExecuteReaderAsync(ct);
		var n = reader.FieldCount;
		while (await reader.ReadAsync(ct))
		{
			var r = new Dictionary<string, object?>(n, StringComparer.Ordinal);
			for (var i = 0; i < n; i++) r[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
			rows.Add(r);
		}
		return new { rows };
	}

	[McpServerTool(Name = "data.exec", Title = "Run SQL exec (INSERT/UPDATE/DELETE/DDL)")]
	[Description("Executes a non-query statement. Returns affected row count. PRAGMA writable_schema / temp_store_directory / data_store_directory / trusted_schema are denied. SQLITE_FULL surfaces as a quota error. Requires data:write scope.")]
	public static async Task<object> ExecAsync(
		IHttpContextAccessor http,
		PetBoxDb db,
		IDataDbFactory factory,
		string projectKey,
		string dbName,
		string sql,
		JsonElement? @params = null,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, ApiKeyScopes.DataWrite);
		AssertNotDeniedPragma(sql);

		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (row is null) throw new InvalidOperationException("DataDb not found");

		var cs = factory.GetConnectionString(projectKey, dbName);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync(ct);
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.CommandTimeout = 30;
		BindJsonParams(cmd, @params);

		var affected = await cmd.ExecuteNonQueryAsync(ct);
		return new { affected };
	}

	// --- Helpers ---------------------------------------------------------

	static void AssertProject(IHttpContextAccessor accessor, string projectKey)
	{
		var ctx = accessor.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (string.IsNullOrEmpty(claim) || !string.Equals(claim, projectKey, StringComparison.Ordinal))
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

	static readonly HashSet<string> DeniedPragmas = new(StringComparer.OrdinalIgnoreCase)
	{
		"writable_schema", "temp_store_directory", "data_store_directory", "trusted_schema",
	};

	static void AssertNotDeniedPragma(string sql)
	{
		var trimmed = sql.TrimStart();
		if (!trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)) return;
		var rest = trimmed.AsSpan(6).TrimStart();
		var end = 0;
		while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '_')) end++;
		if (end == 0) return;
		var pragmaName = rest[..end].ToString();
		if (DeniedPragmas.Contains(pragmaName))
			throw new InvalidOperationException($"PRAGMA {pragmaName} is not allowed");
	}

	static void BindJsonParams(SqliteCommand cmd, JsonElement? @params)
	{
		if (@params is null || @params.Value.ValueKind != JsonValueKind.Array) return;
		foreach (var el in @params.Value.EnumerateArray())
		{
			if (el.ValueKind != JsonValueKind.Object) continue;
			if (!el.TryGetProperty("name", out var nameEl)) continue;
			var name = nameEl.GetString();
			if (string.IsNullOrEmpty(name)) continue;
			var p = cmd.CreateParameter();
			p.ParameterName = name;
			p.Value = el.TryGetProperty("value", out var valEl) ? ConvertJson(valEl) : DBNull.Value;
			cmd.Parameters.Add(p);
		}
	}

	static object ConvertJson(JsonElement el) => el.ValueKind switch
	{
		JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		JsonValueKind.Number when el.TryGetInt64(out var l) => l,
		JsonValueKind.Number => el.GetDouble(),
		JsonValueKind.String => el.GetString() ?? (object)DBNull.Value,
		_ => el.GetRawText(),
	};
}
