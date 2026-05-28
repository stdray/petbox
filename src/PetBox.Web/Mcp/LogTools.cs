using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Kusto.Language;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;

namespace PetBox.Web.Mcp;

// Single MCP tool: KQL query against project's LogDb. This is the only
// real use case for log access from an agent. List-of-services can be
// derived via `events | summarize count() by ServiceKey`; ingest is for
// pets via /api/ingest/clef, not agents.
[McpServerToolType]
public static class LogTools
{
	[McpServerTool(Name = "log.query", Title = "Run KQL query against a project's logs", ReadOnly = true)]
	[Description("Executes a KQL (Kusto Query Language) query against the project's LogDb. Returns either { kind: 'events', events: [...] } for plain queries or { kind: 'table', columns: [...], rows: [[...]] } for shape-changing pipelines (summarize, project, etc.). Requires logs:query scope.")]
	public static async Task<object> QueryAsync(
		IHttpContextAccessor http,
		ILogDbFactory logFactory,
		[Description("Project key — must match the calling ApiKey's project claim.")] string projectKey,
		[Description("KQL query, e.g. 'events | where Level == 4 | take 50' or 'events | summarize count() by ServiceKey'.")] string kql,
		CancellationToken ct = default)
	{
		AssertProject(http, projectKey);
		AssertScope(http, "logs:query");
		if (string.IsNullOrWhiteSpace(kql)) throw new ArgumentException("kql is required");

		KustoCode code;
		try
		{
			code = KustoCode.Parse(kql);
			var parseErrors = code.GetDiagnostics()
				.Where(d => d.Severity == "Error")
				.ToList();
			if (parseErrors.Count > 0)
				throw new ArgumentException("KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message)));
		}
		catch (ArgumentException) { throw; }
		catch (Exception ex)
		{
			throw new ArgumentException(ex.Message);
		}

		var logDb = logFactory.GetLogDb(projectKey);

		try
		{
			if (KqlTransformer.HasShapeChangingOps(code))
			{
				var result = KqlTransformer.Execute(logDb.LogEntries, code);
				var columns = result.Columns.Select(c => c.Name).ToList();
				var rows = new List<List<object?>>();
				await foreach (var row in result.Rows.WithCancellation(ct))
					rows.Add(row.Select(cell => (object?)cell).ToList());
				return new { kind = "table", columns, rows };
			}

			var records = KqlTransformer.Apply(logDb.LogEntries, code);
			var list = await records.ToListAsync(ct);
			var events = list.Select(r =>
			{
				var e = r.ToEntry();
				return new
				{
					id = e.Id,
					serviceKey = e.ServiceKey,
					timestamp = e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
					level = e.Level.ToString(),
					message = e.Message,
					messageTemplate = e.MessageTemplate,
					exception = e.Exception,
					properties = e.GetProperties().ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.Serialize(kv.Value)),
				};
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
}
