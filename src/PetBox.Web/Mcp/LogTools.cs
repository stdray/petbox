using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Log.Core.Query;

namespace PetBox.Web.Mcp;

// Single MCP tool: KQL query against a project's named log. A thin adapter over
// ILogQueryService (the shared execution path, also used by the REST log endpoint);
// it must not open the log context directly (a NetArchTest enforces this). List of
// services is derivable via `events | summarize count() by ServiceKey`; ingest is for
// pets via /api/ingest/clef, not agents.
[McpServerToolType]
public static class LogTools
{
	[McpServerTool(Name = "log.query", Title = "Run KQL query against a named log", ReadOnly = true)]
	[Description("Executes a KQL (Kusto Query Language) query against one named log in a project. Returns either { kind: 'events', events: [...] } for plain queries or { kind: 'table', columns: [...], rows: [[...]] } for shape-changing pipelines (summarize, project, etc.). Requires logs:query scope.")]
	public static async Task<object> QueryAsync(
		IHttpContextAccessor http,
		ILogQueryService logs,
		[Description("Project key — must match the calling ApiKey's project claim.")] string projectKey,
		[Description("Log name within the project (e.g. 'default', 'audit').")] string logName,
		[Description("KQL query, e.g. 'events | where Level == 4 | take 50' or 'events | summarize count() by ServiceKey'.")] string kql,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, ApiKeyScopes.LogsQuery);

		LogQueryResult result;
		try
		{
			result = await logs.QueryAsync(projectKey, logName, kql, ct);
		}
		catch (KqlParseException ex) { throw new ArgumentException(ex.Message); }
		catch (LogNotFoundException ex) { throw new InvalidOperationException(ex.Message); }
		// Non-shape-changing queries materialize inside QueryAsync, so an unsupported-subset
		// error surfaces here, not in the enumeration below — map it the same way.
		catch (UnsupportedKqlException ex) { throw new ArgumentException(ex.Message); }

		try
		{
			if (result is LogQueryResult.Table table)
			{
				var columns = table.Result.Columns.Select(c => c.Name).ToList();
				var rows = new List<List<object?>>();
				await foreach (var row in table.Result.Rows.WithCancellation(ct))
					rows.Add(row.Select(cell => (object?)cell).ToList());
				return new { kind = "table", columns, rows };
			}

			var events = ((LogQueryResult.Events)result).Items.Select(e => new
			{
				id = e.Id,
				serviceKey = e.ServiceKey,
				timestamp = e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
				level = e.Level.ToString(),
				message = e.Message,
				messageTemplate = e.MessageTemplate,
				exception = e.Exception,
				properties = e.GetProperties().ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.Serialize(kv.Value)),
			}).ToList();
			return new { kind = "events", count = events.Count, events };
		}
		catch (UnsupportedKqlException ex)
		{
			throw new InvalidOperationException(ex.Message);
		}
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
