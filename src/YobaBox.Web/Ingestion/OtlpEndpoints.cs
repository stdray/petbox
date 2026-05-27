using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Ingestion;

namespace YobaBox.Web.Ingestion;

public static class OtlpEndpoints
{
	public static void MapOtlpEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/v1/logs", IngestLogs).RequireAuthorization("ApiKey");
		app.MapPost("/v1/traces", IngestTraces).RequireAuthorization("ApiKey");
	}

	static async Task<IResult> IngestLogs(
		HttpContext ctx,
		YobaBoxDb yobaBoxDb,
		IIngestionPipeline pipeline,
		CancellationToken ct)
	{
		var serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(serviceKey))
			return Results.BadRequest("X-Service-Key header required");

		var service = yobaBoxDb.Services.FirstOrDefault(s => s.Key == serviceKey);
		var projectKey = service?.ProjectKey ?? "$system";
		if (service is null)
		{
#pragma warning disable CA2016
			await yobaBoxDb.InsertAsync(new Service
#pragma warning restore CA2016
			{
				Key = serviceKey,
				ProjectKey = "$system",
				HealthModel = HealthModel.Endpoint,
				Health = ServiceHealth.Unknown,
			});
		}

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpLogsParser.Parse(body, serviceKey);
		if (result.IsMalformed)
			return Results.BadRequest(new { error = "malformed protobuf body" });

		if (result.Candidates.Count > 0)
			await pipeline.IngestAsync(projectKey, result.Candidates, ct);

		return Results.Ok(new { ingested = result.Candidates.Count, errors = result.Errors });
	}

	static async Task<IResult> IngestTraces(
		HttpContext ctx,
		YobaBoxDb yobaBoxDb,
		ILogDbFactory logFactory,
		CancellationToken ct)
	{
		// For traces we still need a project to write into. Convention: use X-Project-Key
		// (preferred) or fall back to X-Service-Key → project lookup. Resource attributes
		// like service.name may also identify it but we don't trust them as the routing key.
		var projectKey = ctx.Request.Headers["X-Project-Key"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(projectKey))
		{
			var serviceKey = ctx.Request.Headers["X-Service-Key"].FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(serviceKey))
			{
				var service = yobaBoxDb.Services.FirstOrDefault(s => s.Key == serviceKey);
				projectKey = service?.ProjectKey;
			}
		}
		if (string.IsNullOrWhiteSpace(projectKey))
			return Results.BadRequest("X-Project-Key or X-Service-Key header required");

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpTracesParser.Parse(body);
		if (result.IsMalformed)
			return Results.BadRequest(new { error = "malformed protobuf body" });

		if (result.Spans.Count > 0)
		{
			var logDb = logFactory.GetLogDb(projectKey);
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
