using LinqToDB.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Contract;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;

namespace PetBox.Web.Ingestion;

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
			using var logDb = store.NewContext(LogNames.SystemProject, LogNames.SelfLog);
			await logDb.Spans.BulkCopyAsync(result.Spans, ct);
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
			using var logDb = store.NewContext(LogNames.SystemProject, LogNames.SelfLog);
			await logDb.MetricPoints.BulkCopyAsync(result.Points, ct);
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

	static async Task<IResult> IngestLogs(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		IIngestionPipeline pipeline,
		CancellationToken ct)
	{
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
		CancellationToken ct)
	{
		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'; create it first"));

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpTracesParser.Parse(body);
		if (result.IsMalformed)
			return Results.BadRequest(new ErrorResponse("malformed protobuf body"));

		if (result.Spans.Count > 0)
		{
			using var logDb = store.NewContext(projectKey, logName);
			await logDb.Spans.BulkCopyAsync(result.Spans, ct);
		}

		return Results.Ok(new IngestResponse(result.Spans.Count, result.Errors));
	}

	static async Task<IResult> IngestMetrics(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		CancellationToken ct)
	{
		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new ErrorResponse($"log '{logName}' not found in project '{projectKey}'; create it first"));

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpMetricsParser.Parse(body);
		if (result.IsMalformed)
			return Results.BadRequest(new ErrorResponse("malformed protobuf body"));

		if (result.Points.Count > 0)
		{
			using var logDb = store.NewContext(projectKey, logName);
			await logDb.MetricPoints.BulkCopyAsync(result.Points, ct);
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
