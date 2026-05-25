using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kusto.Language;
using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Models;
using YobaBox.Log.Core.Query;
using YobaBox.Log.Core.Sharing;

namespace YobaBox.Log.Core;

public sealed record ShareCreateRequest(
	string ProjectKey,
	string Kql,
	int TtlMinutes,
	string[]? Columns,
	Dictionary<string, MaskMode>? Modes);

public static class ShareApi
{
	public static void MapShareEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/share", CreateShareAsync).RequireAuthorization();
		app.MapGet("/api/share/{token}/tsv", GetTsvAsync).AllowAnonymous();
	}

	static async Task<IResult> CreateShareAsync(
		HttpContext ctx,
		YobaBoxDb db,
		ShareCreateRequest req,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(req.ProjectKey) || string.IsNullOrWhiteSpace(req.Kql))
			return Results.BadRequest(new { error = "ProjectKey and Kql required" });

		var salt = RandomNumberGenerator.GetBytes(32);
		var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
		var ttl = req.TtlMinutes > 0 ? req.TtlMinutes : 1440;

		var columns = req.Columns is { Length: > 0 } ? req.Columns
			: ["Timestamp", "Level", "ServiceKey", "Message"];

		var modes = req.Modes ?? new Dictionary<string, MaskMode>();

		var entity = new ShareLink
		{
			Id = id,
			ProjectKey = req.ProjectKey,
			Kql = req.Kql,
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = DateTime.UtcNow.AddMinutes(ttl),
			SaltBase64 = Convert.ToBase64String(salt),
			ColumnsJson = JsonSerializer.Serialize(columns),
			ModesJson = JsonSerializer.Serialize(modes),
			CreatedBy = ctx.User.Identity?.Name ?? "system",
		};

		await db.InsertAsync(entity, token: ct);
		return Results.Ok(new { id, expiresAt = entity.ExpiresAt });
	}

	static async Task<IResult> GetTsvAsync(
		HttpContext ctx,
		YobaBoxDb db,
		ILogDbFactory logFactory,
		string token,
		CancellationToken ct)
	{
		var share = await db.ShareLinks.FirstOrDefaultAsync((ShareLink s) => s.Id == token, ct);
		if (share is null) return Results.NotFound();
		if (share.ExpiresAt < DateTime.UtcNow) return Results.NotFound(new { error = "link expired" });

		KustoCode code;
		try { code = KustoCode.Parse(share.Kql); }
		catch { return Results.BadRequest(new { error = "invalid stored KQL" }); }

		var columns = JsonSerializer.Deserialize<string[]>(share.ColumnsJson) ?? [];
		var modesDict = JsonSerializer.Deserialize<Dictionary<string, MaskMode>>(share.ModesJson) ?? [];
		var policy = new FieldMaskingPolicy(modesDict.ToImmutableDictionary(
			kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
		var masker = new ValueMasker(Convert.FromBase64String(share.SaltBase64));

		var logDb = logFactory.GetLogDb(share.ProjectKey);
		var records = await KqlTransformer.Apply(logDb.LogEntries, code).ToListAsync(ct);
		var entries = records.Select(r => r.ToEntry()).ToList();

		ctx.Response.Headers.ContentType = "text/tab-separated-values; charset=utf-8";
		await using var writer = new StreamWriter(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
		await TsvExporter.WriteAsync(ToAsync(entries, ct), columns, policy, masker, writer, ct);
		return Results.Empty;
	}

	static async IAsyncEnumerable<LogEntry> ToAsync(
		IEnumerable<LogEntry> source,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
	{
		foreach (var e in source)
		{
			if (ct.IsCancellationRequested) yield break;
			yield return e;
			await Task.Yield();
		}
	}
}
