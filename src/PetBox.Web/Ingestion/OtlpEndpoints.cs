using LinqToDB.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;

namespace PetBox.Web.Ingestion;

// RETRY IDEMPOTENCY (compat-ingest): a stock OTLP exporter re-sends the identical batch on any
// timeout/5xx, so ingest must be replayable. Spans and metric points are written through
// InsertOrIgnoreAsync (SQLite `INSERT OR IGNORE`) against their natural keys — SpanId for a span,
// (MetricName, MetricType, TimeUnixNs, AttributesJson) for a metric point (log-tier M002). A replay
// is therefore a 200 with no new rows, where it used to be a 500 (span PK conflict) or a silent
// duplicate (metric points had no key at all).
//
// `ingested` in the response stays the count of points/spans ACCEPTED from the payload, not the
// number of rows physically inserted: it answers "did you take my batch?", which is the question the
// exporter asks and the only one it can act on. A replay that stored nothing new is still a fully
// accepted batch.
public static class OtlpEndpoints
{
	public static void MapOtlpEndpoints(this IEndpointRouteBuilder app)
	{
		// Path-based: the destination log is explicit in the URL. X-Service-Key tags
		// the emitter (free string, no Service entity).
		app.MapPost("/v1/logs/{projectKey}/{logName}", IngestLogs)
			.Produces<IngestResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");
		app.MapPost("/v1/traces/{projectKey}/{logName}", IngestTraces)
			.Produces<IngestResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");
		app.MapPost("/v1/metrics/{projectKey}/{logName}", IngestMetrics)
			.Produces<IngestResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");

		// Bare OTLP paths for PetBox's OWN self-export: the standard OTLP exporter posts
		// to {endpoint}/v1/traces|/v1/logs and authenticates with X-Seq-ApiKey. Route to
		// the $system self-log, validating the key against the configured Seq:SelfLog:ApiKey
		// (mirrors the Seq self-log endpoint /api/events/raw).
		app.MapPost("/v1/traces", SelfIngestTraces)
			.Produces<IngestResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.AllowAnonymous();
		app.MapPost("/v1/logs", SelfIngestLogs)
			.Produces<IngestResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.AllowAnonymous();
		app.MapPost("/v1/metrics", SelfIngestMetrics)
			.Produces<IngestResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.AllowAnonymous();
	}

	static IResult? ValidateSelfKey(HttpContext ctx, IConfiguration config)
	{
		var configured = config["Seq:SelfLog:ApiKey"];
		var sent = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault();
		return string.IsNullOrWhiteSpace(configured) || !string.Equals(sent, configured, StringComparison.Ordinal)
			? Results.Unauthorized()
			: null;
	}

	static async Task<IResult> SelfIngestTraces(HttpContext ctx, ILogStore store, IConfiguration config, CancellationToken ct)
	{
		if (ValidateSelfKey(ctx, config) is { } unauth) return unauth;

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpTracesParser.Parse(body);
		if (result.IsMalformed)
			return Results.BadRequest(new ErrorResponse("malformed protobuf body"));

		if (result.Spans.Count > 0)
		{
			// Request-owned connection: concurrent /v1/traces posts (and the self-log
			// writer loop on the same file) racing one cached DataConnection is what
			// produced ObjectDisposedException on sqlite3_stmt.
			using var logDb = store.NewEnsuredContext(LogNames.SystemProject, LogNames.SelfLog);
			await logDb.InsertOrIgnoreAsync(result.Spans, ct);
		}
		return Results.Ok(new IngestResponse(result.Spans.Count, result.Errors));
	}

	static async Task<IResult> SelfIngestMetrics(HttpContext ctx, ILogStore store, IConfiguration config, CancellationToken ct)
	{
		if (ValidateSelfKey(ctx, config) is { } unauth) return unauth;

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpMetricsParser.Parse(body);
		if (result.IsMalformed)
			return Results.BadRequest(new ErrorResponse("malformed protobuf body"));

		if (result.Points.Count > 0)
		{
			// Request-owned connection: concurrent /v1/metrics posts (and the self-log
			// writer loop on the same file) racing one cached DataConnection is what
			// produced ObjectDisposedException on sqlite3_stmt.
			using var logDb = store.NewEnsuredContext(LogNames.SystemProject, LogNames.SelfLog);
			await logDb.InsertOrIgnoreAsync(result.Points, ct);
		}
		return Results.Ok(new IngestResponse(result.Points.Count, result.Errors));
	}

	static async Task<IResult> SelfIngestLogs(HttpContext ctx, IIngestionPipeline pipeline, IConfiguration config, CancellationToken ct)
	{
		if (ValidateSelfKey(ctx, config) is { } unauth) return unauth;

		var serviceKey = config["Seq:SelfLog:ServiceKey"] ?? "petbox-web";
		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpLogsParser.Parse(body, serviceKey);
		if (result.IsMalformed)
			return Results.BadRequest(new ErrorResponse("malformed protobuf body"));

		if (result.Candidates.Count > 0)
			await pipeline.IngestAsync(LogNames.SystemProject, LogNames.SelfLog, result.Candidates, ct);
		return Results.Ok(new IngestResponse(result.Candidates.Count, result.Errors));
	}

	// The "ApiKey" policy on these three routes only proves SOME api key authenticated — it does
	// NOT compare the caller's project claim to the route's {projectKey}, unlike the equivalent
	// CLEF/Seq ingest handlers in LogApi.cs (AuthorizeProject). Without this, any api key could
	// inject OTLP logs/traces/metrics into any project. The bare self-export routes below
	// (SelfIngest*, AllowAnonymous + shared-secret X-Seq-ApiKey) are a deliberately different,
	// unauthenticated-by-design contract and are left untouched.
	static async Task<IResult?> AuthorizeProjectAsync(
		HttpContext ctx, string projectKey, IProjectCatalog catalog, CancellationToken ct) =>
		await ProjectScope.AuthorizesAsync(ctx.User, projectKey, catalog, ct) ? null : Results.Forbid();

	static async Task<IResult> IngestLogs(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		IIngestionPipeline pipeline,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;

		var serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(serviceKey))
			return Results.BadRequest(new ErrorResponse("X-Service-Key header required"));

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'; create it first"));

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpLogsParser.Parse(body, serviceKey);
		if (result.IsMalformed)
			return Results.BadRequest(new ErrorResponse("malformed protobuf body"));

		if (result.Candidates.Count > 0)
			await pipeline.IngestAsync(projectKey, logName, result.Candidates, ct);

		return Results.Ok(new IngestResponse(result.Candidates.Count, result.Errors));
	}

	static async Task<IResult> IngestTraces(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'; create it first"));

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpTracesParser.Parse(body);
		if (result.IsMalformed)
			return Results.BadRequest(new ErrorResponse("malformed protobuf body"));

		if (result.Spans.Count > 0)
		{
			using var logDb = store.NewEnsuredContext(projectKey, logName);
			await logDb.InsertOrIgnoreAsync(result.Spans, ct);
		}

		return Results.Ok(new IngestResponse(result.Spans.Count, result.Errors));
	}

	static async Task<IResult> IngestMetrics(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		if (await AuthorizeProjectAsync(ctx, projectKey, catalog, ct) is { } forbid) return forbid;

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'; create it first"));

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpMetricsParser.Parse(body);
		if (result.IsMalformed)
			return Results.BadRequest(new ErrorResponse("malformed protobuf body"));

		if (result.Points.Count > 0)
		{
			using var logDb = store.NewEnsuredContext(projectKey, logName);
			await logDb.InsertOrIgnoreAsync(result.Points, ct);
		}

		return Results.Ok(new IngestResponse(result.Points.Count, result.Errors));
	}

	static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken ct)
	{
		using var ms = new MemoryStream();
		await body.CopyToAsync(ms, ct);
		return ms.ToArray();
	}
}
