using System.ComponentModel;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Data;
using YobaBox.Data.Schema;

namespace YobaBox.Web.Mcp;

// MCP tools for the Data module. Each tool maps 1:1 onto a /api/data/* REST
// endpoint — same auth (X-Api-Key flows through the standard pipeline before
// reaching the tool), same scopes (data:read / data:write / data:schema), same
// project-claim cross-check.
//
// We do NOT proxy through the HTTP handlers — that'd serialize through the
// network just to come back. Instead, tools call the same underlying services
// (YobaBoxDb, IDataDbFactory, SchemaRunner) that the REST handlers use.
//
// Naming: dot-separated namespace + verb (`data.list_dbs`) so multiple modules
// can coexist without collisions when Config / Log tools land later.
[McpServerToolType]
public static class DataTools
{
	[McpServerTool(Name = "data.list_dbs", Title = "List DataDbs", ReadOnly = true)]
	[Description("Lists all DataDb entries for the project. Returns name + description + quota + timestamps. Requires data:read scope.")]
	public static async Task<object> ListDbsAsync(
		IHttpContextAccessor http,
		YobaBoxDb db,
		[Description("Project key — must match the calling ApiKey's project claim.")] string projectKey,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, "data:read");

		var rows = await db.DataDbs
			.Where(d => d.ProjectKey == projectKey)
			.OrderBy(d => d.Name)
			.Select(d => new { d.Name, d.Description, d.MaxPageCount, d.CreatedAt, d.UpdatedAt })
			.ToListAsync(ct);
		return new { dbs = rows };
	}

	[McpServerTool(Name = "data.create_db", Title = "Create DataDb")]
	[Description("Creates a new DataDb. Returns the resolved metadata. Requires data:schema scope.")]
	public static async Task<object> CreateDbAsync(
		IHttpContextAccessor http,
		YobaBoxDb db,
		IDataDbFactory factory,
		[Description("Project key.")] string projectKey,
		[Description("DataDb name. Must match ^[a-z][a-z0-9_-]{0,99}$ and not be a reserved name.")] string name,
		[Description("Optional human description.")] string? description = null,
		[Description("Max SQLite pages (×4KB) for quota. Default 262144 ≈ 1GB. Minimum 1024.")] long? maxPageCount = null,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, "data:schema");

		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");

		var exists = await db.DataDbs.AnyAsync((DataDb d) => d.ProjectKey == projectKey && d.Name == name, ct);
		if (exists) throw new InvalidOperationException($"DataDb '{name}' already exists");

		var quota = maxPageCount ?? DataDbFactory.DefaultMaxPageCount;
		await factory.CreateAsync(projectKey, name, quota, ct);

		var now = DateTime.UtcNow;
		await db.InsertAsync(new DataDb
		{
			ProjectKey = projectKey,
			Name = name,
			Description = description,
			MaxPageCount = quota,
			CreatedAt = now,
			UpdatedAt = now,
		}, token: ct);

		return new { name, description, maxPageCount = quota, createdAt = now };
	}

	[McpServerTool(Name = "data.delete_db", Title = "Delete DataDb", Destructive = true)]
	[Description("Removes the DataDbs metadata row immediately. File is best-effort deleted; orphan cleanup service mops up locked files. Requires data:schema scope.")]
	public static async Task<object> DeleteDbAsync(
		IHttpContextAccessor http,
		YobaBoxDb db,
		IDataDbFactory factory,
		string projectKey,
		string name,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, "data:schema");

		var deleted = await db.DataDbs.Where(d => d.ProjectKey == projectKey && d.Name == name).DeleteAsync(ct);
		if (deleted == 0) throw new InvalidOperationException("DataDb not found");
		factory.TryDelete(projectKey, name);
		return new { deleted = true, name };
	}

	[McpServerTool(Name = "data.describe_db", Title = "Describe DataDb tables", ReadOnly = true)]
	[Description("Introspects the SQLite file via PRAGMA: returns each non-system, non-journal table with its columns. Requires data:read scope.")]
	public static async Task<object> DescribeDbAsync(
		IHttpContextAccessor http,
		YobaBoxDb db,
		IDataDbFactory factory,
		string projectKey,
		string dbName,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, "data:read");

		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (row is null) throw new InvalidOperationException("DataDb not found");

		var cs = factory.GetConnectionString(projectKey, dbName);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync(ct);

		var tables = new List<object>();
		await using (var cmd = conn.CreateCommand())
		{
			cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' "
				+ "AND name NOT LIKE 'sqlite_%' AND name <> @journal ORDER BY name";
			var p = cmd.CreateParameter();
			p.ParameterName = "@journal";
			p.Value = SchemaRunner.JournalTableName;
			cmd.Parameters.Add(p);
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
				tables.Add(new { name = reader.GetString(0) });
		}

		var result = new List<object>();
		foreach (var t in tables.Cast<dynamic>())
		{
			var cols = new List<object>();
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = $"PRAGMA table_info({t.name})";
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
				cols.Add(new { name = reader.GetString(1), type = reader.GetString(2), notNull = reader.GetInt32(3) == 1, pk = reader.GetInt32(5) > 0 });
			result.Add(new { name = (string)t.name, columns = cols });
		}
		return new { tables = result };
	}

	[McpServerTool(Name = "data.schema_apply", Title = "Apply schema migration", Idempotent = true)]
	[Description("Applies a named SQL migration via DbUp + hash-based idempotency. Re-applying with same name+sql is a no-op; same name with different sql is a 409-style conflict. Requires data:schema scope.")]
	public static async Task<object> SchemaApplyAsync(
		IHttpContextAccessor http,
		YobaBoxDb db,
		IDataDbFactory factory,
		SchemaRunner runner,
		string projectKey,
		string dbName,
		[Description("Migration script name. Used as journal key — same name = same migration.")] string name,
		[Description("SQL to apply. Multi-statement OK; PRAGMA statements may not parse with the SQLite dialect parser.")] string sql,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, "data:schema");

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
		YobaBoxDb db,
		IDataDbFactory factory,
		string projectKey,
		string dbName,
		string sql,
		[Description("Optional parameter list as a JSON array of { name, value }. Pet builds via linq2db's ToSqlQuery().Parameters.")] JsonElement? @params = null,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, "data:read");

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
		YobaBoxDb db,
		IDataDbFactory factory,
		string projectKey,
		string dbName,
		string sql,
		JsonElement? @params = null,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, "data:write");
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
