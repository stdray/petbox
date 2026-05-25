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
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Ingestion;
using YobaBox.Log.Core.Query;

namespace YobaBox.Log.Core;

public static class LogApi
{
	public static void MapLogEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/ingest/clef", IngestClefAsync).RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/query", QueryLogsAsync).RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/services", GetServicesAsync).RequireAuthorization("ApiKey");
		app.MapGet("/api/logs/{projectKey}/live-tail", LiveTailAsync).RequireAuthorization("ApiKey");
	}

	public static void MapSeqSelfLogEndpoint(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/events/raw", SeqIngestAsync).AllowAnonymous();
	}

	static async Task<IResult> IngestClefAsync(
		HttpContext ctx,
		YobaBoxDb yobaBoxDb,
		ILogDbFactory logFactory,
		CleFParser parser,
		ITailBroadcaster broadcaster,
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

		var results = await parser.ParseAsync(ctx.Request.Body, ct)
			.ToListAsync(ct);

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

		var records = candidates
			.Select(c => LogEntryRecord.FromCandidate(c, LogEntryRecord.ComputeTemplateHash(c.MessageTemplate)))
			.ToList();

		var logDb = logFactory.GetLogDb(projectKey);
		await logDb.LogEntries.BulkCopyAsync(records, ct);

		broadcaster.Publish(projectKey, records);

		return Results.Ok(new { ingested = records.Count, errors = errors.Count });
	}

	static async Task<IResult> QueryLogsAsync(
		HttpContext ctx,
		string projectKey,
		ILogDbFactory logFactory,
		CancellationToken ct)
	{
		var kql = ctx.Request.Query["q"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(kql))
			return Results.BadRequest("q parameter required");

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

		var logDb = logFactory.GetLogDb(projectKey);

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
		YobaBoxDb yobaBoxDb,
		CancellationToken ct)
	{
		var services = await yobaBoxDb.Services
			.Where(s => s.ProjectKey == projectKey)
			.Select(s => s.Key)
			.ToListAsync(ct);

		return Results.Json(services);
	}

	static async Task<IResult> SeqIngestAsync(
		HttpContext ctx,
		YobaBoxDb yobaBoxDb,
		ILogDbFactory logFactory,
		CleFParser parser,
		ITailBroadcaster broadcaster,
		IConfiguration config,
		CancellationToken ct)
	{
		var apiKey = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault();
		if (string.IsNullOrWhiteSpace(apiKey))
			return Results.Unauthorized();

		var key = await yobaBoxDb.ApiKeys
			.FirstOrDefaultAsync((ApiKey k) => k.Key == apiKey, CancellationToken.None);
		if (key is null)
			return Results.Unauthorized();

		var serviceKey = config["Seq:SelfLog:ServiceKey"] ?? "yobabox-web";

		var results = await parser.ParseAsync(ctx.Request.Body, ct)
			.ToListAsync(ct);

		var candidates = results
			.Where(r => r.IsSuccess)
			.Select(r => r.Event!)
			.Select(c => c with { ServiceKey = serviceKey })
			.ToList();

		var records = candidates
			.Select(c => LogEntryRecord.FromCandidate(c, LogEntryRecord.ComputeTemplateHash(c.MessageTemplate)))
			.ToList();

		if (records.Count > 0)
		{
			var logDb = logFactory.GetLogDb("$system");
			await logDb.LogEntries.BulkCopyAsync(records, ct);
			broadcaster.Publish("$system", records);
		}

		return Results.Ok();
	}

	static async Task<IResult> LiveTailAsync(
		HttpContext ctx,
		string projectKey,
		ITailBroadcaster broadcaster,
		CancellationToken ct)
	{
		ctx.Response.Headers.ContentType = "text/event-stream";
		ctx.Response.Headers.CacheControl = "no-cache";
		ctx.Response.Headers["X-Accel-Buffering"] = "no";
		await ctx.Response.Body.FlushAsync(ct);

		try
		{
			await foreach (var record in broadcaster.Subscribe(projectKey, ct).ConfigureAwait(false))
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
