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
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Sharing;

namespace PetBox.Log.Core;

public sealed record ShareCreateRequest(
	string ProjectKey,
	string Kql,
	int TtlMinutes,
	string[]? Columns,
	Dictionary<string, MaskMode>? Modes,
	string? LogName = null);

public static class ShareApi
{
	public static void MapShareEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/share", CreateShareAsync)
			.Accepts<ShareCreateRequest>("application/json")
			.Produces<ShareCreatedResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization();
		app.MapGet("/api/share/{token}/tsv", GetTsvAsync).AllowAnonymous();
	}

	static async Task<IResult> CreateShareAsync(
		HttpContext ctx,
		PetBoxDb db,
		ShareCreateRequest req,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(req.ProjectKey) || string.IsNullOrWhiteSpace(req.Kql))
			return Results.BadRequest(new ErrorResponse("ProjectKey and Kql required"));

		// req.ProjectKey comes from the JSON body (attacker-controlled) — bare .RequireAuthorization()
		// only proves SOME authenticated identity, not that it's authorized for THIS project. Without
		// this, any authenticated caller could mint a share link (served anonymously at GetTsvAsync)
		// exporting another project's log data. Same ProjectScope.Authorizes pattern as SessionApi.
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, req.ProjectKey))
			return Results.Forbid();

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
			LogName = string.IsNullOrWhiteSpace(req.LogName) ? LogNames.Default : req.LogName,
			Kql = req.Kql,
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = DateTime.UtcNow.AddMinutes(ttl),
			SaltBase64 = Convert.ToBase64String(salt),
			ColumnsJson = JsonSerializer.Serialize(columns),
			ModesJson = JsonSerializer.Serialize(modes),
			CreatedBy = ctx.User.Identity?.Name ?? "system",
		};

		await db.InsertAsync(entity, token: ct);
		return Results.Ok(new ShareCreatedResponse(id, entity.ExpiresAt));
	}

	static async Task<IResult> GetTsvAsync(
		HttpContext ctx,
		PetBoxDb db,
		ILogStore store,
		string token,
		CancellationToken ct)
	{
		var share = await db.ShareLinks.FirstOrDefaultAsync((ShareLink s) => s.Id == token, ct);
		if (share is null) return Results.NotFound();
		if (share.ExpiresAt < DateTime.UtcNow) return Results.NotFound(new ErrorResponse("link expired"));

		KustoCode code;
		try { code = KustoCode.Parse(share.Kql); }
		catch { return Results.BadRequest(new ErrorResponse("invalid stored KQL")); }

		var columns = JsonSerializer.Deserialize<string[]>(share.ColumnsJson) ?? [];
		var modesDict = JsonSerializer.Deserialize<Dictionary<string, MaskMode>>(share.ModesJson) ?? [];
		var policy = new FieldMaskingPolicy(modesDict.ToImmutableDictionary(
			kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
		var masker = new ValueMasker(Convert.FromBase64String(share.SaltBase64));

		using var logDb = store.GetContext(share.ProjectKey, share.LogName);
		// Memory guard only (KqlLimits.MaxTake, no default take): a share exports whatever its
		// stored KQL selects, but never an unbounded materialization on the small prod VM.
		var records = await KqlTransformer.Apply(logDb.LogEntries, code).Take(KqlLimits.MaxTake).ToListAsync(ct);
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
