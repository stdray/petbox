using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;

namespace PetBox.Web.Ingestion;

public static class OtlpEndpoints
{
	public static void MapOtlpEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/v1/logs/{projectKey}/{logName}", IngestLogsPath).RequireAuthorization("ApiKey");
		app.MapPost("/v1/logs", IngestLogsLegacy).RequireAuthorization("ApiKey");
		app.MapPost("/v1/traces/{projectKey}/{logName}", IngestTracesPath).RequireAuthorization("ApiKey");
		app.MapPost("/v1/traces", IngestTracesLegacy).RequireAuthorization("ApiKey");
	}

	// --- Logs ------------------------------------------------------------

	static Task<IResult> IngestLogsPath(
		HttpContext ctx,
		string projectKey,
		string logName,
		PetBoxDb yobaBoxDb,
		ILogStore store,
		IIngestionPipeline pipeline,
		CancellationToken ct) =>
		IngestLogsCore(ctx, projectKey, logName, yobaBoxDb, store, pipeline, ct);

	static async Task<IResult> IngestLogsLegacy(
		HttpContext ctx,
		PetBoxDb yobaBoxDb,
		ILogStore store,
		IIngestionPipeline pipeline,
		CancellationToken ct)
	{
		var serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(serviceKey))
			return Results.BadRequest("X-Service-Key header required");

		var service = await yobaBoxDb.Services.FirstOrDefaultAsync(s => s.Key == serviceKey, ct);
		if (service is null)
			return Results.BadRequest(
				$"unknown service '{serviceKey}'; use the path-based ingest URL /v1/logs/{{projectKey}}/{{logName}}");

		return await IngestLogsCore(ctx, service.ProjectKey, LogNames.Default, yobaBoxDb, store, pipeline, ct);
	}

	static async Task<IResult> IngestLogsCore(
		HttpContext ctx,
		string projectKey,
		string logName,
		PetBoxDb yobaBoxDb,
		ILogStore store,
		IIngestionPipeline pipeline,
		CancellationToken ct)
	{
		var serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(serviceKey))
			return Results.BadRequest("X-Service-Key header required");

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new { error = $"log '{logName}' not found in project '{projectKey}'; create it first" });

		var service = await yobaBoxDb.Services.FirstOrDefaultAsync(s => s.Key == serviceKey, ct);
		if (service is null)
		{
			await yobaBoxDb.InsertAsync(new Service
			{
				Key = serviceKey,
				ProjectKey = projectKey,
				HealthModel = HealthModel.Endpoint,
				Health = ServiceHealth.Unknown,
			}, token: ct);
		}

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpLogsParser.Parse(body, serviceKey);
		if (result.IsMalformed)
			return Results.BadRequest(new { error = "malformed protobuf body" });

		if (result.Candidates.Count > 0)
			await pipeline.IngestAsync(projectKey, logName, result.Candidates, ct);

		return Results.Ok(new { ingested = result.Candidates.Count, errors = result.Errors });
	}

	// --- Traces ----------------------------------------------------------

	static Task<IResult> IngestTracesPath(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		CancellationToken ct) =>
		IngestTracesCore(ctx, projectKey, logName, store, ct);

	static async Task<IResult> IngestTracesLegacy(
		HttpContext ctx,
		PetBoxDb yobaBoxDb,
		ILogStore store,
		CancellationToken ct)
	{
		// Project from X-Project-Key (preferred) or X-Service-Key → project lookup.
		var projectKey = ctx.Request.Headers["X-Project-Key"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(projectKey))
		{
			var serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(serviceKey))
			{
				var service = await yobaBoxDb.Services.FirstOrDefaultAsync(s => s.Key == serviceKey, ct);
				projectKey = service?.ProjectKey;
			}
		}
		if (string.IsNullOrWhiteSpace(projectKey))
			return Results.BadRequest("X-Project-Key or X-Service-Key header required");

		return await IngestTracesCore(ctx, projectKey, LogNames.Default, store, ct);
	}

	static async Task<IResult> IngestTracesCore(
		HttpContext ctx,
		string projectKey,
		string logName,
		ILogStore store,
		CancellationToken ct)
	{
		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new { error = $"log '{logName}' not found in project '{projectKey}'; create it first" });

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpTracesParser.Parse(body);
		if (result.IsMalformed)
			return Results.BadRequest(new { error = "malformed protobuf body" });

		if (result.Spans.Count > 0)
		{
			var logDb = store.GetContext(projectKey, logName);
			await logDb.Spans.BulkCopyAsync(result.Spans, ct);
		}

		return Results.Ok(new { ingested = result.Spans.Count, errors = result.Errors });
	}

	static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken ct)
	{
		using var ms = new MemoryStream();
		await body.CopyToAsync(ms, ct);
		return ms.ToArray();
	}
}
