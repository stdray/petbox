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
	// Property values land in a REST response body a human/client reads: the default encoder
	// escapes every non-ASCII char (Cyrillic -> \uXXXX). The shared relaxed encoder keeps human
	// text as-is while HTML-sensitive chars stay escaped (parity with the log_query MCP tool).
	static readonly JsonSerializerOptions PropertyJson = new() { Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed };

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
		// The log route a BROWSER opens directly (PetBox.Web's EventDetailsApi is the other one, over
		// in Pages/Logs — it reuses AuthorizeProjectViewerAsync below rather than a route mapped here):
		// a browser's EventSource cannot send headers, so it arrives with the session cookie and nothing
		// else — under the header-only "ApiKey" policy every live tail in the UI 401'd, which is why the
		// feature never delivered a single event (live-tail-sse-transport-broken). The policy admits BOTH
		// schemes; which one authenticated then decides which authorization applies — see
		// AuthorizeProjectViewerAsync, where the two principals are kept strictly apart.
		app.MapGet("/api/logs/{projectKey}/{logName}/live-tail", LiveTailAsync)
			.Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKeyOrCookie");
		app.MapGet("/api/logs/{projectKey}/{logName}/services", GetServicesAsync)
			.Produces<List<string>>()
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");
	}

	// spec log-retention-cascade: RetentionDays is OPTIONAL — omitted/null means "no override",
	// the log is swept by the project/workspace/system cascade exactly as before this field existed.
	public sealed record CreateLogRequest(string Name, string? Description, int? RetentionDays = null);
	public sealed record LogInfo(string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt, int? RetentionDays = null);

	static async Task<IResult> CreateLogAsync(
		HttpContext ctx, string projectKey, CreateLogRequest req, ILogStore store, IProjectCatalog catalog, CancellationToken ct)
	{
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsAdmin)) return Results.Forbid();
		if (req is null || string.IsNullOrWhiteSpace(req.Name))
			return Results.BadRequest(new ErrorResponse("name is required"));

		try
		{
			var meta = await store.CreateAsync(projectKey, req.Name.Trim(), req.Description, req.RetentionDays, ct);
			return Results.Created(
				$"/api/logs/{projectKey}/logs/{meta.Name}",
				new LogInfo(meta.Name, meta.Description, meta.CreatedAt, meta.UpdatedAt, meta.RetentionDays));
		}
		catch (ArgumentException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
		catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
		{
			return Results.Conflict(new ErrorResponse(ex.Message));
		}
		catch (InvalidOperationException ex) { return Results.NotFound(new ErrorResponse(ex.Message)); }
	}

	static async Task<IResult> ListLogsAsync(
		HttpContext ctx, string projectKey, ILogStore store, IProjectCatalog catalog, CancellationToken ct)
	{
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsQuery)) return Results.Forbid();

		var rows = (await store.ListAsync(projectKey, ct))
			.Select(l => new LogInfo(l.Name, l.Description, l.CreatedAt, l.UpdatedAt, l.RetentionDays))
			.ToList();
		return Results.Ok(rows);
	}

	static async Task<IResult> DeleteLogAsync(
		HttpContext ctx, string projectKey, string name, ILogStore store, IProjectCatalog catalog, CancellationToken ct)
	{
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsAdmin)) return Results.Forbid();
		if (projectKey == LogNames.SystemProject && name == LogNames.SelfLog)
			return Results.BadRequest(new ErrorResponse("the petbox self-log cannot be deleted"));

		var deleted = await store.DeleteAsync(projectKey, name, ct);
		return deleted ? Results.NoContent() : Results.NotFound(new ErrorResponse("log not found"));
	}

	static async Task<IResult?> AuthorizeProjectAsync(
		HttpContext ctx, string projectKey, IProjectCatalog catalog, CancellationToken ct) =>
		await ProjectScope.AuthorizesAsync(ctx.User, projectKey, catalog, ct) ? null : Results.Forbid();

	static bool HasScope(HttpContext ctx, string required) =>
		HasScope(ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "", required);

	// Authorization for the browser-facing, workspaceKey-less log routes — live-tail and (PetBox.Web's)
	// EventDetailsApi both call this, since both are the SAME cross-tenant surface: neither route binds
	// a {workspaceKey}, so this is the ONLY thing standing between a signed-in user and another tenant's
	// log data. Public so PetBox.Web can reuse it verbatim rather than re-deriving the same two gates.
	//
	// The route accepts both schemes (a browser EventSource/fetch can only bring a cookie), but they
	// prove entirely different things and each keeps its own gate:
	//
	//   api key — carries `project` + `scopes`. Unchanged from before this endpoint accepted cookies:
	//             ProjectScope (project claim + sandbox containment) AND the logs:query scope, exactly
	//             what QueryLogsAsync demands. Nothing here weakens the key path.
	//   cookie  — carries NEITHER a project claim nor scopes; it carries workspace-role claims. So it
	//             is authorized the way the logs PAGE authorizes it: the project's OWNING workspace
	//             (asked of the catalog — the route has no {workspaceKey} to bind against, unlike the
	//             pages ProjectWorkspaceBindingFilter covers) must be one this session holds at least
	//             Viewer in; sysadmin keeps its free pass. A session with no role in that workspace is
	//             refused — this is the cross-tenant surface of the whole change.
	//
	// Crossing the two would be the hole: run a cookie session through the scope gate and every browser
	// is denied (a cookie has no scopes at all); run an api key through the workspace gate and a key
	// lacking logs:query walks in through a door meant for humans.
	public static async Task<IResult?> AuthorizeProjectViewerAsync(
		HttpContext ctx, string projectKey, IProjectCatalog catalog, CancellationToken ct)
	{
		if (IsApiKeyPrincipal(ctx))
		{
			if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;
			return HasScope(ctx, ApiKeyScopes.LogsQuery) ? null : Results.Forbid();
		}

		var workspaceKey = await catalog.WorkspaceKeyOfAsync(projectKey, ct);
		// IsNullOrEmpty, not `is null`: a project row's WorkspaceKey defaults to "" in the model, not
		// null. Fail-closed either way today (no membership carries an empty workspace key, so
		// HasWorkspaceRoleAtLeast("") would find no role and Forbid regardless) — this just removes the
		// dependency on that being true forever, so a future default change can't quietly open the gate.
		if (string.IsNullOrEmpty(workspaceKey)) return Results.Forbid();
		return ctx.User.HasWorkspaceRoleAtLeast(workspaceKey, WorkspaceRole.Viewer) ? null : Results.Forbid();
	}

	// An api-key request is one the ApiKey SCHEME authenticated — asked of the identity, not of the
	// presence of a claim: `ClaimsPrincipal.Identity` is merely the FIRST identity, and a policy that
	// lists two schemes merges both when both authenticate, so which one lands first is an ordering
	// detail no authorization decision may rest on.
	static bool IsApiKeyPrincipal(HttpContext ctx) =>
		ctx.User.Identities.Any(i => i.IsAuthenticated
			&& string.Equals(i.AuthenticationType, ApiKeyAuthenticationHandler.SchemeName, StringComparison.Ordinal));

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
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		// The "ApiKey" policy only proves SOME api key authenticated — every OTHER handler in
		// this file (CreateLogAsync/ListLogsAsync/DeleteLogAsync/QueryLogsAsync/GetServicesAsync/
		// LiveTailAsync, plus SeqIngestPathAsync's manual checks below) additionally verifies the
		// key's project claim authorizes THIS route's projectKey and carries the right scope; this
		// handler was missing both, so any logs:* key from any project could ingest into any
		// project's named log via the path-based CLEF route.
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsIngest)) return Results.Forbid();

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
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;
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
				return await WriteShapeChangedResult(table.Result, table.Truncation, ct);

			var eventsResult = (LogQueryResult.Events)queryResult;
			var events = eventsResult.Items;
			var dtos = events.Select(e => new LogEventDto(
				e.Id,
				e.ServiceKey,
				e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
				e.Level.ToString(),
				e.Message,
				e.MessageTemplate,
				e.Exception,
				e.GetProperties().ToDictionary(kv => kv.Key, kv => JsonSerializer.Serialize(kv.Value, PropertyJson)))).ToList();
			return Results.Json(new LogEventsResponse(dtos.Count, dtos, eventsResult.Truncated));
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

	static async Task<IResult> WriteShapeChangedResult(KqlResult result, TruncationSignal truncation, CancellationToken ct)
	{
		var columns = result.Columns.Select(c => c.Name).ToImmutableArray();
		var rows = new List<ImmutableArray<JsonElement?>>();
		await foreach (var row in result.Rows.WithCancellation(ct))
		{
			var arr = ImmutableArray.CreateRange(row.Select(cell =>
				cell is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(cell)));
			rows.Add(arr);
		}
		// The signal is final only after the enumeration above.
		return Results.Json(new KqlTableResponse(columns, rows, truncation.Truncated));
	}

	static async Task<IResult> GetServicesAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;
		if (!HasScope(ctx, ApiKeyScopes.LogsQuery)) return Results.Forbid();

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'"));

		using var logDb = store.NewEnsuredContext(projectKey, logName);
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
		IApiKeyLookup lookup,
		ILogStore store,
		CleFParser parser,
		IIngestionPipeline pipeline,
		IConfiguration config,
		IProjectCatalog catalog,
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
			// Same door every OTHER handler in this file authenticates through (via the "ApiKey"
			// scheme's ApiKeyAuthenticationHandler) — this endpoint is AllowAnonymous and reads a
			// Seq-specific header instead of X-Api-Key, so it cannot go through the scheme, but the
			// lookup itself is identical: one indexed core.db read behind IApiKeyLookup (config keys
			// first, in-memory, then DbApiKeyLookup's fresh caller-owned connection) — same cost as
			// the pre-conversion `dbf.Open()` + `ApiKeys.FirstOrDefaultAsync`, not a new hop.
			var key = lookup.FindByKey(apiKey);
			if (key is null || (key.ExpiresAt is { } exp && exp <= DateTime.UtcNow))
				return Results.Unauthorized();
			// Explicit 403 (not Results.Forbid(), which on this AllowAnonymous endpoint
			// would invoke the cookie scheme's challenge and 302-redirect to /Login).
			if (!HasScope(key.Scopes, ApiKeyScopes.LogsIngest))
				return Results.Json(
					new ErrorResponse($"key lacks the '{ApiKeyScopes.LogsIngest}' scope"),
					statusCode: StatusCodes.Status403Forbidden);

			projectKey = key.ProjectKey;
			// The destination is the key's OWN project, so the claim check is trivially
			// satisfied — but containment is not: re-check it live rather than leaning on the
			// mint-time invariant, exactly as SeqIngestPathAsync does (a future flip of
			// Project.Sandbox would silently turn that invariant into a hole).
			var access = await ProjectScope.EvaluateAsync(key.ProjectKey, projectKey, key.SandboxOnly, catalog, ct);
			if (access != ProjectAccess.Allowed)
				return Results.Json(
					new ErrorResponse(access == ProjectAccess.SandboxContainment
						? ProjectScope.SandboxDenialMessage(projectKey, subject: "key")
						: $"key is not authorized for project '{projectKey}'"),
					statusCode: StatusCodes.Status403Forbidden);
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
		IApiKeyLookup lookup,
		ILogStore store,
		CleFParser parser,
		IIngestionPipeline pipeline,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var apiKey = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(apiKey))
			return Results.Unauthorized();

		// Same IApiKeyLookup door as SeqIngestAsync's else-branch — see its comment for why this
		// AllowAnonymous, Seq-header-authenticated handler can't go through the "ApiKey" scheme but
		// costs the identical one-lookup-per-request as the `dbf.Open()` call it replaces.
		var key = lookup.FindByKey(apiKey);
		if (key is null || (key.ExpiresAt is { } exp && exp <= DateTime.UtcNow))
			return Results.Unauthorized();
		// Explicit 403s (not Results.Forbid(), which on this AllowAnonymous endpoint
		// would invoke the cookie scheme's challenge and 302-redirect to /Login).
		if (!HasScope(key.Scopes, ApiKeyScopes.LogsIngest))
			return Results.Json(
				new ErrorResponse($"key lacks the '{ApiKeyScopes.LogsIngest}' scope"),
				statusCode: StatusCodes.Status403Forbidden);
		// This handler authenticates the Seq key manually (AllowAnonymous + X-Seq-ApiKey), so it
		// has no ClaimsPrincipal to read — go through the string-based EvaluateAsync overload
		// directly with the DB row's own ProjectKey/SandboxOnly, same containment guarantee as
		// every claims-based caller (spec work/smoke-writes-into-real-projects).
		var access = await ProjectScope.EvaluateAsync(key.ProjectKey, projectKey, key.SandboxOnly, catalog, ct);
		if (access != ProjectAccess.Allowed)
			return Results.Json(
				new ErrorResponse(access == ProjectAccess.SandboxContainment
					? ProjectScope.SandboxDenialMessage(projectKey, subject: "key")
					: $"key is not authorized for project '{projectKey}'"),
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

	// Rows are re-read from the DB in pages of this size, so a long catch-up streams instead of
	// materializing the whole gap at once.
	const int LiveTailBatch = 200;

	static async Task<IResult> LiveTailAsync(
		HttpContext ctx,
		string projectKey,
		string logName,
		ITailBroadcaster broadcaster,
		ILogStore store,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		if (await AuthorizeProjectViewerAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;
		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'"));

		// The SAME ?kql= the table applies (ts/logs.ts's newestRenderedCursor caller passes
		// target.dataset["kql"], the exact text on screen). Ignoring it used to mean the tail fired a
		// firehose of every event regardless of the filter the user just switched it on to watch through
		// — the filter breaking at exactly the moment it is needed. Validated BEFORE any SSE header is
		// written, so a rejected query gets a normal 400 JSON body instead of an aborted stream.
		var kqlText = ctx.Request.Query["kql"].FirstOrDefault();
		var kql = string.IsNullOrWhiteSpace(kqlText) ? KqlTransformer.EventsTable : kqlText.Trim();
		KustoCode code;
		try { code = KustoCode.Parse(kql); }
		catch (Exception ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
		var parseErrors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
		if (parseErrors.Count > 0)
			return Results.BadRequest(new ErrorResponse("KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message))));
		var root = KqlTransformer.GetRootTableName(code);
		if (root is not null && !string.Equals(root, KqlTransformer.EventsTable, StringComparison.Ordinal))
			return Results.BadRequest(new ErrorResponse(KqlTransformer.UnknownTableMessage(root)));
		// summarize/project/distinct/join/lookup/mv-expand/parse/count reshape or aggregate rows — there
		// is no per-row form of an aggregate to stream (a live tail of "count by Level" is not a stream,
		// it is a moving total that would need recomputing from scratch on every event). The Logs page
		// already hides the live-tail toggle for such a query (Index.cshtml's `@if
		// (!Model.IsShapeChanged)`), so reaching this branch means a hand-built request; it is refused the
		// same way the page would refuse to offer it, with the reason spelled out rather than silently
		// falling back to an unfiltered firehose.
		if (KqlTransformer.HasShapeChangingOps(code))
			return Results.BadRequest(new ErrorResponse(
				"live tail applies row-level filters ('where') only; this query changes the row shape "
				+ "(summarize/project/distinct/join/lookup/mv-expand/parse/count) and has no per-row form to "
				+ "stream — remove it to use live tail, or read this query from the table instead"));

		// Where to resume from. Last-Event-ID beats ?since=: the browser sends it on its OWN automatic
		// reconnect and it is the id: of the last event that actually arrived, which is strictly fresher
		// than the ?since= frozen into the sse-connect URL when the tail was switched on. Both carry the
		// same LogCursor the logs table pages by, so "everything newer than the last row on screen" is
		// one comparison, not a per-surface reinvention.
		var cursor = LogCursor.TryDecode(ctx.Request.Headers["Last-Event-ID"].FirstOrDefault())
			?? LogCursor.TryDecode(ctx.Request.Query["since"].FirstOrDefault());

		using var logDb = store.NewEnsuredContext(projectKey, logName);
		using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);

		// Subscribe BEFORE reading anything, and treat the broadcast purely as a WAKE-UP: every row that
		// goes out is read back from the DB past the cursor, never taken from the broadcast record (a
		// BulkCopy'd LogEntryRecord never gets its identity back — its Id is 0, so it can be neither a
		// row id nor a cursor). The ingestion pipeline INSERTS before it publishes, so with the
		// subscription registered first an event is either already committed (the drain below reads it)
		// or published after the drain (a signal wakes us and the next drain reads it). No third case —
		// that is what closes the gap between the last row the page rendered and the stream opening.
		// It is MoveNextAsync (not GetAsyncEnumerator) that registers the subscriber: the iterator body
		// runs synchronously up to its first await, and it joins the subscriber list before that await.
		await using var signals = broadcaster.Subscribe(projectKey, logName, link.Token).GetAsyncEnumerator(link.Token);
		var pending = signals.MoveNextAsync().AsTask();

		// No cursor = nothing on screen (an empty table, or a filtered view that matched nothing): start
		// at the log's CURRENT tip, i.e. only what happens from now on. That is the old behavior, and the
		// only one that makes sense with nothing rendered to catch up to.
		cursor ??= await TipAsync(logDb, ct);

		ctx.Response.Headers.ContentType = "text/event-stream";
		ctx.Response.Headers.CacheControl = "no-cache";
		ctx.Response.Headers["X-Accel-Buffering"] = "no";
		await ctx.Response.Body.FlushAsync(ct);

		try
		{
			cursor = await DrainAsync(ctx, logDb, cursor.Value, code, ct);
			while (await pending)
			{
				pending = signals.MoveNextAsync().AsTask();
				cursor = await DrainAsync(ctx, logDb, cursor.Value, code, ct);
			}
		}
		catch (OperationCanceledException) { }
		finally
		{
			// A drain that threw (a dead response socket) leaves a MoveNextAsync in flight; disposing the
			// iterator underneath it is undefined. Cancel, let it observe the cancel, then dispose. The
			// Task form is what makes this safe — a consumed ValueTask must not be awaited twice.
			await link.CancelAsync();
			try { await pending; } catch (OperationCanceledException) { }
		}

		return Results.Empty;
	}

	// The newest committed row, by the table's own ordering key.
	static async Task<LogCursor> TipAsync(LogDb logDb, CancellationToken ct)
	{
		var tip = await logDb.LogEntries
			.OrderByDescending(e => e.TimestampMs)
			.ThenByDescending(e => e.Id)
			.FirstOrDefaultAsync(ct);
		return tip is null ? default : new LogCursor(tip.TimestampMs, tip.Id);
	}

	// Everything committed strictly past `cursor` AND matching `kql`'s row filter, oldest first, in
	// pages — and the cursor it ends on. The strict (Timestamp, Id) comparison is what makes equal
	// timestamps safe: rows sharing a millisecond are separated by Id, so none is served twice and none
	// is skipped. Ascending order + the client's hx-swap="afterbegin" puts the newest on top — the same
	// order the table renders.
	//
	// The kql predicate composes with the cursor predicate as ONE query (ApplyRowFilters adds a further
	// .Where() on top of the cursor's), so `Take(LiveTailBatch)` bounds MATCHING rows, not raw rows — a
	// selective filter over a long backlog may scan well past LiveTailBatch raw rows per round trip, but
	// never returns more than LiveTailBatch. The cursor always advances to the last MATCHING row actually
	// sent, so a row that failed the filter is still "passed over" for good once a later matching row
	// moves the cursor beyond it — it is not re-scanned, only never re-considered as a hit.
	static async Task<LogCursor> DrainAsync(HttpContext ctx, LogDb logDb, LogCursor cursor, KustoCode kql, CancellationToken ct)
	{
		while (true)
		{
			var ts = cursor.TimestampMs;
			var id = cursor.Id;
			IQueryable<LogEntryRecord> filtered = logDb.LogEntries
				.Where(e => e.TimestampMs > ts || (e.TimestampMs == ts && e.Id > id));
			filtered = KqlTransformer.ApplyRowFilters(filtered, kql);
			var batch = await filtered
				.OrderBy(e => e.TimestampMs)
				.ThenBy(e => e.Id)
				.Take(LiveTailBatch)
				.ToListAsync(ct);

			if (batch.Count == 0) return cursor;

			foreach (var record in batch)
			{
				cursor = new LogCursor(record.TimestampMs, record.Id);
				await ctx.Response.WriteAsync(RenderEvent(record, cursor), ct);
			}
			await ctx.Response.Body.FlushAsync(ct);

			if (batch.Count < LiveTailBatch) return cursor;
		}
	}

	// One SSE frame. The `id:` field is the row's cursor, so the browser echoes it back as
	// Last-Event-ID when it auto-reconnects and the stream resumes exactly where it stopped.
	static string RenderEvent(LogEntryRecord record, LogCursor cursor)
	{
		var sb = new System.Text.StringBuilder();
		sb.Append("id: ");
		sb.Append(cursor.Encode());
		sb.Append('\n');
		sb.Append("event: event\n");
		sb.Append("data: ");
		// data-testid matches _EventRow.cshtml's non-live row (the "data-testid for UI selectors" hard
		// invariant applies here too) — without it an E2E test has no compliant way to find a live row
		// at all, only a class/text selector the repo's own rules forbid.
		sb.Append("<tr class=\"event-live\" data-testid=\"events-row\" data-event-id=\"");
		sb.Append(record.Id);
		// data-ms matches every other log/trace row template (_EventRow.cshtml, Traces.cshtml,
		// Trace.cshtml) — sub-second precision is data here (event ordering within a second),
		// not noise; see localTime.ts's renderLocalTimes, which reads this attribute per-element.
		sb.Append("\"><td><time class=\"local-time\" data-ms datetime=\"");
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
		return sb.ToString();
	}
}
