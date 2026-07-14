using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Log.Core.Data;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Named-log catalog lifecycle MCP tools (log_create / log_list / log_update / log_delete) — de-collapsed
// from the old generic entity.* tools into typed per-type tools (typed-surface Phase 4).
// Kept in its OWN type, separate from LogTools, so LogTools (log_query) stays free of an
// ILogStore / LogDb dependency (a NetArchTest enforces that — log_query must go through
// ILogQueryService). This type owns the catalog the same way RelationTools owns relations.
// Scopes: logs:admin (create/delete) / logs:query (list), project-scoped. Tools throw on a
// failed Assert*; McpErrorEnvelopeFilter renders the exception as the structured {error} body.
[McpServerToolType]
public static class LogCatalogTools
{
	[McpServerTool(Name = "log_create", Title = "Create a named log", UseStructuredContent = true, OutputSchemaType = typeof(LogCreatedResult))]
	[Description("Creates a named log (its SQLite file + metadata) in a project. Requires logs:admin scope. `retentionDays` (optional) sets this log's OWN retention window in days, overriding the project/workspace/system cascade (spec log-retention-cascade); omit it to keep the log on the cascade like every log before this field existed. Must be positive if given.")]
	public static async Task<LogCreatedResult> CreateAsync(
		IHttpContextAccessor http, ILogStore logStore,
		string projectKey, string name,
		[Description("Optional description.")] string? description = null,
		[Description("Optional per-log retention window in days. Omit to use the project/workspace/system cascade.")] int? retentionDays = null,
		CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.LogsAdmin);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
		if (await logStore.ExistsAsync(projectKey, name, ct))
			throw new InvalidOperationException($"Log '{name}' already exists");
		var meta = await logStore.CreateAsync(projectKey, name, description, retentionDays, ct);
		return new LogCreatedResult(meta.Name, meta.Description, meta.CreatedAt, meta.RetentionDays);
	}

	[McpServerTool(Name = "log_list", Title = "List named logs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(LogListResult))]
	[Description("Lists a project's named logs (name, description, timestamps, retentionDays). Requires logs:query scope. `retentionDays` is null when the log has no override and is swept by the project/workspace/system cascade.")]
	public static async Task<LogListResult> ListAsync(
		IHttpContextAccessor http, ILogStore logStore,
		string projectKey, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.LogsQuery);
		var rows = await logStore.ListAsync(projectKey, ct);
		return new LogListResult(rows.Select(l => new LogRow(l.Name, l.Description, l.CreatedAt, l.UpdatedAt, l.RetentionDays)).ToList());
	}

	[McpServerTool(Name = "log_update", Title = "Update a named log's retention window", UseStructuredContent = true, OutputSchemaType = typeof(LogUpdatedResult))]
	[Description("Sets or clears a named log's OWN retention override (spec log-retention-cascade). Requires logs:admin scope. `retentionDays` is REQUIRED: a positive value sets the log's own window (it is then swept by that window regardless of the project/workspace/system cascade); 0 CLEARS the override, reverting the log to the cascade. This does not touch name/description — there is nothing else on a log to patch today.")]
	public static async Task<LogUpdatedResult> UpdateAsync(
		IHttpContextAccessor http, ILogStore logStore,
		string projectKey, string name,
		[Description("Positive = set the log's own retention window in days. 0 = clear the override (revert to the cascade).")] int retentionDays,
		CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.LogsAdmin);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
		var meta = await logStore.UpdateRetentionDaysAsync(projectKey, name, retentionDays, ct)
			?? throw new InvalidOperationException("Log not found");
		return new LogUpdatedResult(meta.Name, meta.RetentionDays);
	}

	[McpServerTool(Name = "log_delete", Title = "Delete a named log", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(LogDeletedResult))]
	[Description("Deletes a named log and its file. Requires logs:admin scope.")]
	public static async Task<LogDeletedResult> DeleteAsync(
		IHttpContextAccessor http, ILogStore logStore,
		string projectKey, string name, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.LogsAdmin);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
		var deleted = await logStore.DeleteAsync(projectKey, name, ct);
		if (!deleted) throw new InvalidOperationException("Log not found");
		return new LogDeletedResult(true, name);
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
