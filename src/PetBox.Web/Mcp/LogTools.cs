using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Log.Core.Query;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Single MCP tool: KQL query against a project's named log. A thin adapter over
// ILogQueryService (the shared execution path, also used by the REST log endpoint);
// it must not open the log context directly (a NetArchTest enforces this — the named-log
// catalog lifecycle lives in LogCatalogTools, which owns the ILogStore dependency). List of
// services is derivable via `events | summarize count() by ServiceKey`; ingest is for
// pets via /api/ingest/clef, not agents. Tool throws on a failed Assert* or a KQL
// parse/not-found/unsupported error; McpErrorEnvelopeFilter renders the structured {error} body.
// Execution faults (engine/translation) arrive as KqlExecutionException — from QueryAsync
// for events, or from the await-foreach over streamed Table rows — and deliberately flow
// to the same envelope, so the agent sees { error: { type, message, detail } } with the
// failure class instead of the framework's opaque "An error occurred invoking 'log_query'.".
[McpServerToolType]
public static class LogTools
{
	// Property values are serialized for a tool result an agent reads: the default encoder
	// escapes every non-ASCII char (Cyrillic -> \uXXXX), making the properties unreadable.
	// The shared relaxed encoder keeps human text as-is while HTML-sensitive chars stay escaped.
	static readonly JsonSerializerOptions PropertyJson = new() { Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed };

	[McpServerTool(Name = "log_query", Title = "Run KQL query against a named log", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(LogQueryResultView))]
	[Description("Executes a KQL (Kusto Query Language) query against one named log in a project. Returns either { kind: 'events', events: [...] } for plain queries or { kind: 'table', columns: [...], rows: [[...]] } for shape-changing pipelines (summarize, project, etc.). Responses are row-capped: no explicit take/top applies a default limit (1000 rows), an explicit one is bounded by a hard max (100k); a cut result carries truncated: true — add/tighten take (or aggregate) to bound the query deliberately. Requires logs:query scope.")]
	public static async Task<LogQueryResultView> QueryAsync(
		IHttpContextAccessor http,
		ILogQueryService logs,
		[Description("Project key — must match the calling ApiKey's project claim.")] string projectKey,
		[Description("Log name within the project (e.g. 'default', 'audit').")] string logName,
		[Description("KQL query, e.g. 'events | where Level == 4 | take 50' or 'events | summarize count() by ServiceKey'.")] string kql,
		CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.LogsQuery);

		LogQueryResult result;
		try
		{
			result = await logs.QueryAsync(projectKey, logName, kql, ct);
		}
		catch (KqlParseException ex) { throw new ArgumentException(ex.Message); }
		catch (LogNotFoundException ex) { throw new InvalidOperationException(ex.Message); }

		try
		{
			if (result is LogQueryResult.Table table)
			{
				var columns = table.Result.Columns.Select(c => c.Name).ToList();
				var rows = new List<IReadOnlyList<object?>>();
				await foreach (var row in table.Result.Rows.WithCancellation(ct))
					rows.Add(row.Select(cell => (object?)cell).ToList());
				// The truncation signal is final only after the enumeration above; omit when false.
				return new LogQueryResultView("table", Columns: columns, Rows: rows,
					Truncated: table.Truncation.Truncated ? true : null);
			}

			var eventsResult = (LogQueryResult.Events)result;
			var events = eventsResult.Items.Select(e => new LogEventView(
				Id: e.Id,
				ServiceKey: e.ServiceKey,
				Timestamp: e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
				Level: e.Level.ToString(),
				Message: e.Message,
				MessageTemplate: e.MessageTemplate,
				Exception: e.Exception,
				Properties: e.GetProperties().ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.Serialize(kv.Value, PropertyJson)))).ToList();
			return new LogQueryResultView("events", Count: events.Count, Events: events,
				Truncated: eventsResult.Truncated ? true : null);
		}
		catch (UnsupportedKqlException ex)
		{
			throw new InvalidOperationException(ex.Message);
		}
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
