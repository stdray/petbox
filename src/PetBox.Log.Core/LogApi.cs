using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;
using PetBox.Log.Core.Query;

namespace PetBox.Log.Core;

public static class LogApi
{
	public static void MapLogEndpoints(this IEndpointRouteBuilder app)
	{
		// Ingestion. Path-based carries the destination log explicitly so one
		// project-scoped key can write to many named logs. X-Service-Key tags the
		// emitter (a free string, no Service entity).
		app.MapPost("/api/ingest/{projectKey}/{logName}/clef", IngestClefPathAsync).RequireAuthorization("ApiKey");

		// Lifecycle — create / list / delete named logs (mirrors /api/data/{p}/dbs).
		app.MapPost("/api/logs/{projectKey}/logs", CreateLogAsync).RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/logs", ListLogsAsync).RequireAuthorization("ApiKey");
		app.MapDelete("/api/logs/{projectKey}/logs/{name}", DeleteLogAsync).RequireAuthorization("ApiKey");

		// Read — per named log. services = distinct emitter ServiceKey within the log.
		app.MapGet("/api/logs/{projectKey}/{logName}/query", QueryLogsAsync).RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/{logName}/live-tail", LiveTailAsync).RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/{logName}/services", GetServicesAsync).RequireAuthorization("ApiKey");
	}

	public sealed record CreateLogRequest(string Name, string? Description);
	public sealed record LogInfo(string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt);

	static async Task<IResult> CreateLogAsync(
		HttpContext ctx, string projectKey, CreateLogRequest req, ILogStore store, CancellationToken ct)
	{
		if (!AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsAdmin)) return Results.Forbid();
		if (req is null || string.IsNullOrWhiteSpace(req.Name))
			return Results.BadRequest(new { error = "name is required" });

		try
		{
			var meta = await store.CreateAsync(projectKey, req.Name.Trim(), req.Description, ct);
			return Results.Created(
				$"/api/logs/{projectKey}/logs/{meta.Name}",
				new LogInfo(meta.Name, meta.Description, meta.CreatedAt, meta.UpdatedAt));
		}
		catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
		catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
		{
			return Results.Conflict(new { error = ex.Message });
		}
		catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
	}

	static async Task<IResult> ListLogsAsync(
		HttpContext ctx, string projectKey, ILogStore store, CancellationToken ct)
	{
		if (!AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsQuery)) return Results.Forbid();

		var rows = (await store.ListAsync(projectKey, ct))
			.Select(l => new LogInfo(l.Name, l.Description, l.CreatedAt, l.UpdatedAt))
			.ToList();
		return Results.Ok(rows);
	}

	static async Task<IResult> DeleteLogAsync(
		HttpContext ctx, string projectKey, string name, ILogStore store, CancellationToken ct)
	{
		if (!AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsAdmin)) return Results.Forbid();
		if (projectKey == LogNames.SystemProject && name == LogNames.SelfLog)
			return Results.BadRequest(new { error = "the petbox self-log cannot be deleted" });

		var deleted = await store.DeleteAsync(projectKey, name, ct);
		return deleted ? Results.NoContent() : Results.NotFound(new { error = "log not found" });
	}

	static bool AuthorizeProject(HttpContext ctx, string projectKey, out IResult forbid)
	{
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (string.IsNullOrEmpty(claim) || !string.Equals(claim, projectKey, StringComparison.Ordinal))
		{
			forbid = Results.Forbid();
			return false;
		}
		forbid = null!;
		return true;
	}

	static bool HasScope(HttpContext ctx, string required) =>
		HasScope(ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "", required);

	static bool HasScope(string scopes, string required) =>
		(scopes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Contains(required, StringComparer.Ordinal);

	public static void MapSeqSelfLogEndpoint(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/events/raw", SeqIngestAsync).AllowAnonymous();
	}

	static async Task<IResult> IngestClefPathAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		CleFParser parser,
		IIngestionPipeline pipeline,
		CancellationToken ct)
	{
		var serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(serviceKey))
			return Results.BadRequest("X-Service-Key header required");

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new { error = $"log '{logName}' not found in project '{projectKey}'; create it first" });

		var results = await parser.ParseAsync(ctx.Request.Body, ct).ToListAsync(ct);

		var errors = results.Where(r => !r.IsSuccess).ToList();
		if (errors.Count > 0 && results.All(r => !r.IsSuccess))
			return Results.BadRequest(new
			{
				error = "All lines failed validation",
				details = errors.Select(e => new { line = e.LineNumber, message = e.Error?.Message }),
			});

		var candidates = results
			.Where(r => r.IsSuccess)
			.Select(r => r.Event!)
			.Select(c => c with { ServiceKey = serviceKey })
			.ToList();

		await pipeline.IngestAsync(projectKey, logName, candidates, ct);

		return Results.Ok(new { ingested = candidates.Count, errors = errors.Count });
	}

	static async Task<IResult> QueryLogsAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		CancellationToken ct)
	{
		var kql = ctx.Request.Query["q"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(kql))
			return Results.BadRequest("q parameter required");

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new { error = $"log '{logName}' not found in project '{projectKey}'" });

		KustoCode code;
		try
		{
			code = KustoCode.Parse(kql);
			var parseErrors = code.GetDiagnostics()
				.Where(d => d.Severity == "Error")
				.ToList();
			if (parseErrors.Count > 0)
				return Results.BadRequest(new
				{
					error = "KQL parse error",
					details = parseErrors.Select(d => d.Message),
				});
		}
		catch (Exception ex)
		{
			return Results.BadRequest(new { error = ex.Message });
		}

		var logDb = store.GetContext(projectKey, logName);

		try
		{
			if (KqlTransformer.HasShapeChangingOps(code))
			{
				var result = KqlTransformer.Execute(logDb.LogEntries, code);
				return await WriteShapeChangedResult(result, ct);
			}

			var events = new List<Models.LogEntry>();
			var records = KqlTransformer.Apply(logDb.LogEntries, code);
			var list = await records.ToListAsync(ct);
			foreach (var r in list)
				events.Add(r.ToEntry());

			return Results.Json(new
			{
				count = events.Count,
				events = events.Select(e => new
				{
					id = e.Id,
					serviceKey = e.ServiceKey,
					timestamp = e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
					level = e.Level.ToString(),
					message = e.Message,
					messageTemplate = e.MessageTemplate,
					exception = e.Exception,
					properties = e.GetProperties().ToDictionary(kv => kv.Key, kv => JsonSerializer.Serialize(kv.Value)),
				}),
			});
		}
		catch (UnsupportedKqlException ex)
		{
			return Results.BadRequest(new { error = ex.Message });
		}
	}

	static async Task<IResult> WriteShapeChangedResult(KqlResult result, CancellationToken ct)
	{
		var columns = result.Columns.Select(c => c.Name).ToImmutableArray();
		var rows = new List<ImmutableArray<JsonElement?>>();
		await foreach (var row in result.Rows.WithCancellation(ct))
		{
			var arr = ImmutableArray.CreateRange(row.Select(cell =>
				cell is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(cell)));
			rows.Add(arr);
		}
		return Results.Json(new { columns = (object)columns, rows });
	}

	static async Task<IResult> GetServicesAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		CancellationToken ct)
	{
		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new { error = $"log '{logName}' not found in project '{projectKey}'" });

		var logDb = store.GetContext(projectKey, logName);
		var services = await logDb.LogEntries
			.Select(e => e.ServiceKey)
			.Distinct()
			.OrderBy(s => s)
			.ToListAsync(ct);

		return Results.Json(services);
	}

	// Seq-protocol ingest. winston-seq (and any Seq client) always POSTs here with
	// no log in the URL, so the destination is *header-routed*: the configured
	// self-log key lands in petbox's own $system self-log, while every other
	// (project-scoped) key writes to its project's `default` log — the fallback
	// LogNames.Default exists precisely for this. Previously this hardcoded the
	// $system self-log for ALL keys, so a project's winston-seq lines silently went
	// to petbox's system log instead of the project's.
	static async Task<IResult> SeqIngestAsync(
		HttpContext ctx,
		PetBoxDb yobaBoxDb,
		ILogStore store,
		CleFParser parser,
		IIngestionPipeline pipeline,
		IConfiguration config,
		CancellationToken ct)
	{
		var apiKey = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(apiKey))
			return Results.Unauthorized();

		string projectKey;
		string logName;
		string serviceKey;

		var selfKey = config["Seq:SelfLog:ApiKey"];
		if (!string.IsNullOrWhiteSpace(selfKey) && string.Equals(apiKey, selfKey, StringComparison.Ordinal))
		{
			// petbox's own self-log export.
			projectKey = LogNames.SystemProject;
			logName = LogNames.SelfLog;
			serviceKey = config["Seq:SelfLog:ServiceKey"] ?? "petbox-web";
		}
		else
		{
			var key = await yobaBoxDb.ApiKeys
				.FirstOrDefaultAsync((ApiKey k) => k.Key == apiKey, CancellationToken.None);
			if (key is null || (key.ExpiresAt is { } exp && exp <= DateTime.UtcNow))
				return Results.Unauthorized();
			// Explicit 403 (not Results.Forbid(), which on this AllowAnonymous endpoint
			// would invoke the cookie scheme's challenge and 302-redirect to /Login).
			if (!HasScope(key.Scopes, ApiKeyScopes.LogsIngest))
				return Results.Json(
					new { error = $"key lacks the '{ApiKeyScopes.LogsIngest}' scope" },
					statusCode: StatusCodes.Status403Forbidden);

			projectKey = key.ProjectKey;
			logName = LogNames.Default;
			serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault() is { Length: > 0 } svc ? svc : "seq";

			// Header-routed ingest has no auto-vivify (mirrors path-based): the
			// project's `default` log must exist first.
			if (!await store.ExistsAsync(projectKey, logName, ct))
				return Results.NotFound(new
				{
					error = $"log '{logName}' not found in project '{projectKey}'; create it first " +
						$"(Seq/header-routed ingest targets the project's '{LogNames.Default}' log)",
				});
		}

		var results = await parser.ParseAsync(ctx.Request.Body, ct)
			.ToListAsync(ct);

		var candidates = results
			.Where(r => r.IsSuccess)
			.Select(r => r.Event!)
			.Select(c => c with { ServiceKey = serviceKey })
			.ToList();

		if (candidates.Count > 0)
			await pipeline.IngestAsync(projectKey, logName, candidates, ct);

		return Results.Ok();
	}

	static async Task<IResult> LiveTailAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		ITailBroadcaster broadcaster,
		CancellationToken ct)
	{
		ctx.Response.Headers.ContentType = "text/event-stream";
		ctx.Response.Headers.CacheControl = "no-cache";
		ctx.Response.Headers["X-Accel-Buffering"] = "no";
		await ctx.Response.Body.FlushAsync(ct);

		try
		{
			await foreach (var record in broadcaster.Subscribe(projectKey, logName, ct).ConfigureAwait(false))
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("event: event\n");
				sb.Append("data: ");
				sb.Append("<tr class=\"event-live\" data-event-id=\"");
				sb.Append(record.Id);
				sb.Append("\"><td><time class=\"local-time\" datetime=\"");
				var dt = DateTimeOffset.FromUnixTimeMilliseconds(record.TimestampMs);
				sb.Append(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
				sb.Append("\">");
				sb.Append(dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
				sb.Append("</time></td><td><span class=\"badge badge-xs\">");
				sb.Append(record.Level);
				sb.Append("</span></td><td class=\"text-sm\">");
				sb.Append(WebUtility.HtmlEncode(record.Message ?? string.Empty));
				sb.Append("</td><td class=\"font-mono text-xs\">");
				sb.Append(WebUtility.HtmlEncode(record.ServiceKey ?? string.Empty));
				sb.Append("</td></tr>\n\n");
				await ctx.Response.WriteAsync(sb.ToString(), ct);
				await ctx.Response.Body.FlushAsync(ct);
			}
		}
		catch (OperationCanceledException) { }

		return Results.Empty;
	}
}
