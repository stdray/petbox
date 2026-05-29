using LinqToDB.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;

namespace PetBox.Web.Ingestion;

public static class OtlpEndpoints
{
	public static void MapOtlpEndpoints(this IEndpointRouteBuilder app)
	{
		// Path-based only: the destination log is explicit in the URL. X-Service-Key
		// tags the emitter (free string, no Service entity).
		app.MapPost("/v1/logs/{projectKey}/{logName}", IngestLogs).RequireAuthorization("ApiKey");
		app.MapPost("/v1/traces/{projectKey}/{logName}", IngestTraces).RequireAuthorization("ApiKey");
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
			return Results.BadRequest("X-Service-Key header required");

		if (!await store.ExistsAsync(projectKey, logName, ct))
			return Results.NotFound(new { error = $"log '{logName}' not found in project '{projectKey}'; create it first" });

		var body = await ReadBodyAsync(ctx.Request.Body, ct);
		var result = OtlpLogsParser.Parse(body, serviceKey);
		if (result.IsMalformed)
			return Results.BadRequest(new { error = "malformed protobuf body" });

		if (result.Candidates.Count > 0)
			await pipeline.IngestAsync(projectKey, logName, result.Candidates, ct);

		return Results.Ok(new { ingested = result.Candidates.Count, errors = result.Errors });
	}

	static async Task<IResult> IngestTraces(
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
