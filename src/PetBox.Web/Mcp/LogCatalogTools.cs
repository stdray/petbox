using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Log.Core.Data;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Named-log catalog lifecycle MCP tools (log.create / log.list / log.delete) — de-collapsed
// from the old generic entity.* tools into typed per-type tools (typed-surface Phase 4).
// Kept in its OWN type, separate from LogTools, so LogTools (log.query) stays free of an
// ILogStore / LogDb dependency (a NetArchTest enforces that — log.query must go through
// ILogQueryService). This type owns the catalog the same way RelationTools owns relations.
// Scopes: logs:admin (create/delete) / logs:query (list), project-scoped. Tools throw on a
// failed Assert*; McpErrorEnvelopeFilter renders the exception as the structured {error} body.
[McpServerToolType]
public static class LogCatalogTools
{
	[McpServerTool(Name = "log.create", Title = "Create a named log", UseStructuredContent = true, OutputSchemaType = typeof(LogCreatedResult))]
	[Description("Creates a named log (its SQLite file + metadata) in a project. Requires logs:admin scope.")]
	public static async Task<LogCreatedResult> CreateAsync(
		IHttpContextAccessor http, ILogStore logStore,
		string projectKey, string name,
		[Description("Optional description.")] string? description = null,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, ApiKeyScopes.LogsAdmin);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
		if (await logStore.ExistsAsync(projectKey, name, ct))
			throw new InvalidOperationException($"Log '{name}' already exists");
		var meta = await logStore.CreateAsync(projectKey, name, description, ct);
		return new LogCreatedResult(meta.Name, meta.Description, meta.CreatedAt);
	}

	[McpServerTool(Name = "log.list", Title = "List named logs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(LogListResult))]
	[Description("Lists a project's named logs (name, description, timestamps). Requires logs:query scope.")]
	public static async Task<LogListResult> ListAsync(
		IHttpContextAccessor http, ILogStore logStore,
		string projectKey, CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, ApiKeyScopes.LogsQuery);
		var rows = await logStore.ListAsync(projectKey, ct);
		return new LogListResult(rows.Select(l => new LogRow(l.Name, l.Description, l.CreatedAt, l.UpdatedAt)).ToList());
	}

	[McpServerTool(Name = "log.delete", Title = "Delete a named log", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(LogDeletedResult))]
	[Description("Deletes a named log and its file. Requires logs:admin scope.")]
	public static async Task<LogDeletedResult> DeleteAsync(
		IHttpContextAccessor http, ILogStore logStore,
		string projectKey, string name, CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, ApiKeyScopes.LogsAdmin);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
		var deleted = await logStore.DeleteAsync(projectKey, name, ct);
		if (!deleted) throw new InvalidOperationException("Log not found");
		return new LogDeletedResult(true, name);
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
