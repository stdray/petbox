using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Data.Contract;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// DataDb lifecycle MCP tools (db_create / db_list / db_delete / db_describe) — de-collapsed
// from the old generic entity.* tools into typed per-type tools (typed-surface Phase 4).
// Kept in its OWN type, separate from DataTools (a NetArchTest guards that DataTools carries no
// raw Microsoft.Data.Sqlite dependency).
//
// Neither core.db nor the DataDb files are opened here: the rows-plus-file lifecycle and the
// schema introspection live in IDataDbCatalog, the one door onto the DataDbs catalog (AGENTS.md:
// the database is visible only in the service layer). The project is proven FIRST (AssertProject
// → the key's claim, incl. sandbox containment) and the scope second; the catalog then takes the
// project as part of the address, so a call can only ever reach its own project's DataDbs.
// Tools throw on a failed Assert*; McpErrorEnvelopeFilter renders the structured {error} body.
[McpServerToolType]
public static class DataDbTools
{
	[McpServerTool(Name = "db_create", Title = "Create a DataDb", UseStructuredContent = true, OutputSchemaType = typeof(DataDbCreatedResult))]
	[Description("Creates a named DataDb (user-data SQLite file) in a project. Requires data:schema scope. `maxPageCount` caps the file size (default ~1 GB at 4 KB pages).")]
	public static async Task<DataDbCreatedResult> CreateAsync(
		IHttpContextAccessor http, IDataDbCatalog catalog,
		string projectKey, string name,
		[Description("Optional description.")] string? description = null,
		[Description("Page-count quota (default ~262144 = ~1 GB).")] long? maxPageCount = null,
		CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.DataSchema);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");

		var result = await catalog.CreateAsync(projectKey, name, description, maxPageCount, ct);
		return result switch
		{
			DataDbChangeResult.Created c => new DataDbCreatedResult(c.Db.Name, c.Db.Description, c.Db.MaxPageCount, c.Db.CreatedAt),
			DataDbChangeResult.Conflict k => throw new InvalidOperationException(k.Reason),
			DataDbChangeResult.Refused r => throw new ArgumentException(r.Reason),
			_ => throw new InvalidOperationException("DataDb could not be created"),
		};
	}

	[McpServerTool(Name = "db_list", Title = "List DataDbs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DataDbListResult))]
	[Description("Lists a project's DataDbs (name, description, quota, timestamps). Requires data:read scope.")]
	public static async Task<DataDbListResult> ListAsync(
		IHttpContextAccessor http, IDataDbCatalog catalog,
		string projectKey, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.DataRead);
		var rows = await catalog.ListAsync(projectKey, ct);
		return new DataDbListResult(
			[.. rows.Select(d => new DataDbRow(d.Name, d.Description, d.MaxPageCount, d.CreatedAt, d.UpdatedAt))]);
	}

	[McpServerTool(Name = "db_delete", Title = "Delete a DataDb", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(DataDbDeletedResult))]
	[Description("Deletes a DataDb and its on-disk file. Requires data:schema scope.")]
	public static async Task<DataDbDeletedResult> DeleteAsync(
		IHttpContextAccessor http, IDataDbCatalog catalog,
		string projectKey, string name, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.DataSchema);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");

		var result = await catalog.DeleteAsync(projectKey, name, ct);
		if (result is not DataDbChangeResult.Deleted) throw new InvalidOperationException("DataDb not found");
		return new DataDbDeletedResult(true, name);
	}

	[McpServerTool(Name = "db_describe", Title = "Describe a DataDb", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DataDbDescribeResult))]
	[Description("Returns a DataDb's tables and their columns (name, type, notNull, pk). Requires data:read scope.")]
	public static async Task<DataDbDescribeResult> DescribeAsync(
		IHttpContextAccessor http, IDataDbCatalog catalog,
		string projectKey, string dbName, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.DataRead);

		var tables = await catalog.DescribeAsync(projectKey, dbName, ct)
			?? throw new InvalidOperationException("DataDb not found");

		return new DataDbDescribeResult(
			[.. tables.Select(t => new DataTableView(
				t.Name,
				[.. t.Columns.Select(c => new DataColumnView(c.Name, c.Type, c.NotNull, c.PrimaryKey))]))]);
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
