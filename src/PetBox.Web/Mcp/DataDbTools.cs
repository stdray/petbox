using System.ComponentModel;
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
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// DataDb lifecycle MCP tools (db_create / db_list / db_delete / db_describe) — de-collapsed
// from the old generic entity.* tools into typed per-type tools (typed-surface Phase 4).
// Kept in its OWN type, separate from DataTools, so DataTools stays free of a raw
// Microsoft.Data.Sqlite dependency (a NetArchTest guards that). db_describe legitimately
// introspects the schema over its own connection — same as the old entity.describe did.
// Scopes: data:schema (create/delete) / data:read (list/describe), project-scoped. Tools
// throw on a failed Assert*; McpErrorEnvelopeFilter renders the structured {error} body.
[McpServerToolType]
public static class DataDbTools
{
	[McpServerTool(Name = "db_create", Title = "Create a DataDb", UseStructuredContent = true, OutputSchemaType = typeof(DataDbCreatedResult))]
	[Description("Creates a named DataDb (user-data SQLite file) in a project. Requires data:schema scope. `maxPageCount` caps the file size (default ~1 GB at 4 KB pages).")]
	public static async Task<DataDbCreatedResult> CreateAsync(
		IHttpContextAccessor http, PetBoxDb db, IDataDbFactory factory,
		string projectKey, string name,
		[Description("Optional description.")] string? description = null,
		[Description("Page-count quota (default ~262144 = ~1 GB).")] long? maxPageCount = null,
		CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.DataSchema);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
		if (await db.DataDbs.AnyAsync((DataDb d) => d.ProjectKey == projectKey && d.Name == name, ct))
			throw new InvalidOperationException($"DataDb '{name}' already exists");

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
		return new DataDbCreatedResult(name, description, quota, now);
	}

	[McpServerTool(Name = "db_list", Title = "List DataDbs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DataDbListResult))]
	[Description("Lists a project's DataDbs (name, description, quota, timestamps). Requires data:read scope.")]
	public static async Task<DataDbListResult> ListAsync(
		IHttpContextAccessor http, PetBoxDb db,
		string projectKey, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.DataRead);
		var rows = await db.DataDbs
			.Where(d => d.ProjectKey == projectKey)
			.OrderBy(d => d.Name)
			.Select(d => new DataDbRow(d.Name, d.Description, d.MaxPageCount, d.CreatedAt, d.UpdatedAt))
			.ToListAsync(ct);
		return new DataDbListResult(rows);
	}

	[McpServerTool(Name = "db_delete", Title = "Delete a DataDb", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(DataDbDeletedResult))]
	[Description("Deletes a DataDb and its on-disk file. Requires data:schema scope.")]
	public static async Task<DataDbDeletedResult> DeleteAsync(
		IHttpContextAccessor http, PetBoxDb db, IDataDbFactory factory,
		string projectKey, string name, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.DataSchema);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
		var deleted = await db.DataDbs.Where(d => d.ProjectKey == projectKey && d.Name == name).DeleteAsync(ct);
		if (deleted == 0) throw new InvalidOperationException("DataDb not found");
		factory.TryDelete(projectKey, name);
		return new DataDbDeletedResult(true, name);
	}

	[McpServerTool(Name = "db_describe", Title = "Describe a DataDb", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DataDbDescribeResult))]
	[Description("Returns a DataDb's tables and their columns (name, type, notNull, pk). Requires data:read scope.")]
	public static async Task<DataDbDescribeResult> DescribeAsync(
		IHttpContextAccessor http, PetBoxDb db, IDataDbFactory factory,
		string projectKey, string dbName, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.DataRead);
		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (row is null) throw new InvalidOperationException("DataDb not found");

		var cs = factory.GetConnectionString(projectKey, dbName);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync(ct);

		var names = new List<string>();
		await using (var cmd = conn.CreateCommand())
		{
			cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' "
				+ "AND name NOT LIKE 'sqlite_%' AND name <> @journal ORDER BY name";
			var p = cmd.CreateParameter();
			p.ParameterName = "@journal";
			p.Value = SchemaRunner.JournalTableName;
			cmd.Parameters.Add(p);
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
		}

		var tables = new List<DataTableView>();
		foreach (var tableName in names)
		{
			var cols = new List<DataColumnView>();
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
				cols.Add(new DataColumnView(reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1, reader.GetInt32(5) > 0));
			tables.Add(new DataTableView(tableName, cols));
		}
		return new DataDbDescribeResult(tables);
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
