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
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
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
		app.MapPost("/api/ingest/{projectKey}/{logName}/clef", IngestClefPathAsync)
			.Produces<IngestResponse>()
			.Produces<IngestRejectedResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");

		// Compat ingest — stock foreign-protocol clients into a NAMED log. Every
		// mimicked protocol lives under …/compat/{protocol} (one place to see who we
		// impersonate); the stock client appends its own paths to that base. Seq
		// clients append `api/events/raw` to serverUrl and send only X-Seq-ApiKey,
		// so auth is in-handler (the "ApiKey" policy reads other headers) and
		// X-Service-Key is optional, unlike the path-based CLEF route above.
		app.MapPost("/api/ingest/{projectKey}/{logName}/compat/seq/api/events/raw", SeqIngestPathAsync)
			.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.AllowAnonymous();

		// Lifecycle — create / list / delete named logs (mirrors /api/data/{p}/dbs).
		app.MapPost("/api/logs/{projectKey}/logs", CreateLogAsync)
			.Accepts<CreateLogRequest>("application/json")
			.Produces<LogInfo>(StatusCodes.Status201Created)
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.Produces<ErrorResponse>(StatusCodes.Status409Conflict)
			.RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/logs", ListLogsAsync)
			.Produces<List<LogInfo>>()
			.RequireAuthorization("ApiKey");
		app.MapDelete("/api/logs/{projectKey}/logs/{name}", DeleteLogAsync)
			.Produces(StatusCodes.Status204NoContent)
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");

		// Read — per named log. services = distinct emitter ServiceKey within the log.
		app.MapGet("/api/logs/{projectKey}/{logName}/query", QueryLogsAsync)
			.Produces<LogEventsResponse>()
			.Produces<KqlTableResponse>()
			.Produces<KqlParseErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.Produces<KqlExecutionErrorResponse>(StatusCodes.Status500InternalServerError)
			.RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/{logName}/live-tail", LiveTailAsync).RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/{logName}/services", GetServicesAsync)
			.Produces<List<string>>()
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");
	}

	public sealed record CreateLogRequest(string Name, string? Description);
	public sealed record LogInfo(string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt);

	static async Task<IResult> CreateLogAsync(
		HttpContext ctx, string projectKey, CreateLogRequest req, ILogStore store, CancellationToken ct)
	{
		if (!AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsAdmin)) return Results.Forbid();
		if (req is null || string.IsNullOrWhiteSpace(req.Name))
			return Results.BadRequest(new ErrorResponse("name is required"));

		try
		{
			var meta = await store.CreateAsync(projectKey, req.Name.Trim(), req.Description, ct);
			return Results.Created(
				$"/api/logs/{projectKey}/logs/{meta.Name}",
				new LogInfo(meta.Name, meta.Description, meta.CreatedAt, meta.UpdatedAt));
		}
		catch (ArgumentException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
		catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
		{
			return Results.Conflict(new ErrorResponse(ex.Message));
		}
		catch (InvalidOperationException ex) { return Results.NotFound(new ErrorResponse(ex.Message)); }
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
			return Results.BadRequest(new ErrorResponse("the petbox self-log cannot be deleted"));

		var deleted = await store.DeleteAsync(projectKey, name, ct);
		return deleted ? Results.NoContent() : Results.NotFound(new ErrorResponse("log not found"));
	}

	static bool AuthorizeProject(HttpContext ctx, string projectKey, out IResult forbid)
	{
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
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

	// Parse a CLEF ingest body into per-event results. Two wire formats are accepted
	// (parity with yobalog's ingest, lost in the merge to PetBox):
	//   1. application/vnd.serilog.clef (or unspecified): NDJSON — one CLEF event per line.
	//   2. application/json: a {"Events":[…]} envelope (seq-logging 3.x / @datalust/winston-seq).
	//      Inner events are either CLEF (@t…) or seq-logging's legacy Raw shape
	//      (Timestamp/Level/MessageTemplate/Exception/Properties), normalised by RawEventEnvelope.
	static async Task<List<CleFLineResult>> ParseIngestBodyAsync(
		HttpContext ctx, CleFParser parser, CancellationToken ct)
	{
		var contentType = ctx.Request.ContentType ?? "";
		var isJsonEnvelope = contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
			&& !contentType.Contains("clef", StringComparison.OrdinalIgnoreCase);

		if (!isJsonEnvelope)
			return await parser.ParseAsync(ctx.Request.Body, ct).ToListAsync(ct);

		var results = new List<CleFLineResult>();
		JsonDocument doc;
		try
		{
			doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
		}
		catch (JsonException ex)
		{
			results.Add(CleFLineResult.Failure(1, CleFErrorKind.MalformedJson, ex.Message));
			return results;
		}
		using (doc)
		{
			if (doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty("Events", out var events)
				|| events.ValueKind != JsonValueKind.Array)
			{
				results.Add(CleFLineResult.Failure(1, CleFErrorKind.MalformedJson,
					"expected {\"Events\": [...]} envelope for application/json"));
				return results;
			}

			var idx = 0;
			foreach (var evt in events.EnumerateArray())
			{
				idx++;
				var lineJson = evt.TryGetProperty("@t", out _) ? evt.GetRawText() : RawEventEnvelope.ToClefLine(evt);
				results.Add(CleFParser.ParseLine(lineJson, idx));
			}
		}
		return results;
	}

	public static void MapSeqSelfLogEndpoint(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/events/raw", SeqIngestAsync)
			.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.AllowAnonymous();
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
			return Results.BadRequest(new ErrorResponse("X-Service-Key header required"));

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'; create it first"));

		var results = await ParseIngestBodyAsync(ctx, parser, ct);

		var errors = results.Where(r => !r.IsSuccess).ToList();
		if (errors.Count > 0 && results.All(r => !r.IsSuccess))
			return Results.BadRequest(new IngestRejectedResponse(
				"All lines failed validation",
				errors.Select(e => new IngestLineError(e.LineNumber, e.Error?.Message)).ToList()));

		var candidates = results
			.Where(r => r.IsSuccess)
			.Select(r => r.Event!)
			.Select(c => c with { ServiceKey = serviceKey })
			.ToList();

		await pipeline.IngestAsync(projectKey, logName, candidates, ct);

		return Results.Ok(new IngestResponse(candidates.Count, errors.Count));
	}

	static async Task<IResult> QueryLogsAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogQueryService query,
		CancellationToken ct)
	{
		if (!AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsQuery)) return Results.Forbid();

		var kql = ctx.Request.Query["q"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(kql))
			return Results.BadRequest(new ErrorResponse("q parameter required"));

		LogQueryResult queryResult;
		try
		{
			queryResult = await query.QueryAsync(projectKey, logName, kql, ct);
		}
		catch (LogNotFoundException ex)
		{
			return Results.NotFound(new ErrorResponse(ex.Message));
		}
		catch (KqlParseException ex)
		{
			return Results.BadRequest(new KqlParseErrorResponse("KQL parse error", ex.Details));
		}
		// KqlTransformer.Apply throws synchronously inside QueryAsync (e.g. a query not
		// rooted at 'events') — a user error, not a server fault.
		catch (UnsupportedKqlException ex)
		{
			return Results.BadRequest(new ErrorResponse(ex.Message));
		}
		// Engine faults (linq2db translation, SQLite) during events materialization —
		// and anything else unexpected — must reach an API caller as structured JSON,
		// never as the HTML /Error page.
		catch (OperationCanceledException) { throw; }
		catch (Exception ex)
		{
			return ExecutionError(ex);
		}

		try
		{
			if (queryResult is LogQueryResult.Table table)
				return await WriteShapeChangedResult(table.Result, ct);

			var events = ((LogQueryResult.Events)queryResult).Items;
			var dtos = events.Select(e => new LogEventDto(
				e.Id,
				e.ServiceKey,
				e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
				e.Level.ToString(),
				e.Message,
				e.MessageTemplate,
				e.Exception,
				e.GetProperties().ToDictionary(kv => kv.Key, kv => JsonSerializer.Serialize(kv.Value)))).ToList();
			return Results.Json(new LogEventsResponse(dtos.Count, dtos));
		}
		catch (UnsupportedKqlException ex)
		{
			return Results.BadRequest(new ErrorResponse(ex.Message));
		}
		// A shape-changing result streams — engine faults surface HERE, in the
		// await-foreach over Rows (buffered before the response starts, so a JSON
		// error result is still writable).
		catch (OperationCanceledException) { throw; }
		catch (Exception ex)
		{
			return ExecutionError(ex);
		}
	}

	// Structured 500 for a query that parsed fine but failed to EXECUTE. User errors
	// stay 400 above; Type carries the ORIGINATING exception (the engine fault inside
	// KqlExecutionException, or the exception itself) for machine-readable branching.
	static IResult ExecutionError(Exception ex) =>
		Results.Json(
			new KqlExecutionErrorResponse(
				ex.Message,
				(ex is KqlExecutionException ? ex.InnerException?.GetType() : null)?.Name ?? ex.GetType().Name),
			statusCode: StatusCodes.Status500InternalServerError);

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
		return Results.Json(new KqlTableResponse(columns, rows));
	}

	static async Task<IResult> GetServicesAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		CancellationToken ct)
	{
		if (!AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsQuery)) return Results.Forbid();

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'"));

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
					new ErrorResponse($"key lacks the '{ApiKeyScopes.LogsIngest}' scope"),
					statusCode: StatusCodes.Status403Forbidden);

			projectKey = key.ProjectKey;
			logName = LogNames.Default;
			serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault() is { Length: > 0 } svc ? svc : "seq";

			// Header-routed ingest has no auto-vivify (mirrors path-based): the
			// project's `default` log must exist first.
			if (!await store.ExistsAsync(projectKey, logName, ct))
				return Results.NotFound(new ErrorResponse(
					$"log '{logName}' not found in project '{projectKey}'; create it first " +
					$"(Seq/header-routed ingest targets the project's '{LogNames.Default}' log)"));
		}

		var results = await ParseIngestBodyAsync(ctx, parser, ct);

		var candidates = results
			.Where(r => r.IsSuccess)
			.Select(r => r.Event!)
			.Select(c => c with { ServiceKey = serviceKey })
			.ToList();

		if (candidates.Count > 0)
			await pipeline.IngestAsync(projectKey, logName, candidates, ct);

		return Results.Ok();
	}

	// Seq-protocol ingest into a NAMED log: serverUrl = …/api/ingest/{p}/{log}/compat/seq
	// works with a stock Seq client (Serilog.Sinks.Seq / Seq.Extensions.Logging) and a
	// regular project API key — zero client-side custom code. Mirrors SeqIngestAsync's
	// project-key branch, with the destination taken from the route instead of falling
	// back to the `default` log; no self-log special case here.
	static async Task<IResult> SeqIngestPathAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		PetBoxDb yobaBoxDb,
		ILogStore store,
		CleFParser parser,
		IIngestionPipeline pipeline,
		CancellationToken ct)
	{
		var apiKey = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(apiKey))
			return Results.Unauthorized();

		var key = await yobaBoxDb.ApiKeys
			.FirstOrDefaultAsync((ApiKey k) => k.Key == apiKey, CancellationToken.None);
		if (key is null || (key.ExpiresAt is { } exp && exp <= DateTime.UtcNow))
			return Results.Unauthorized();
		// Explicit 403s (not Results.Forbid(), which on this AllowAnonymous endpoint
		// would invoke the cookie scheme's challenge and 302-redirect to /Login).
		if (!HasScope(key.Scopes, ApiKeyScopes.LogsIngest))
			return Results.Json(
				new ErrorResponse($"key lacks the '{ApiKeyScopes.LogsIngest}' scope"),
				statusCode: StatusCodes.Status403Forbidden);
		if (!ProjectScope.Authorizes(key.ProjectKey, projectKey))
			return Results.Json(
				new ErrorResponse($"key is not authorized for project '{projectKey}'"),
				statusCode: StatusCodes.Status403Forbidden);

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse(
				$"log '{logName}' not found in project '{projectKey}'; create it first"));

		var serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault() is { Length: > 0 } svc ? svc : "seq";

		var results = await ParseIngestBodyAsync(ctx, parser, ct);

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
		if (!AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsQuery)) return Results.Forbid();

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
