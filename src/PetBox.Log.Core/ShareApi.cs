using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kusto.Language;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

// The revoke body carries the SAME field CreateShareAsync's body carries — deliberately, so the
// [TenantFrom(BodyField, "projectKey")] declaration below reuses the identical mechanism rather than
// inventing a second way to name a tenant on this class.
public sealed record ShareDeleteRequest(string ProjectKey);

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
		app.MapDelete("/api/share/{token}", DeleteShareAsync)
			.Accepts<ShareDeleteRequest>("application/json")
			.Produces<DeletedResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization();
	}

	// req.ProjectKey comes from the JSON BODY, and bare .RequireAuthorization() only proves SOME
	// authenticated identity — not that it is authorized for THIS project. Without a tenant check any
	// authenticated caller could mint a share link (served ANONYMOUSLY at GetTsvAsync) exporting another
	// project's log data, which makes this the sharpest body tenant in the tree. It is declared as one:
	// [TenantFrom(BodyField, "projectKey")], read out of the body by TenantEnforcementMiddleware
	// case-insensitively (the model binder is too) before the handler runs. That replaced the
	// ProjectScope.AuthorizesAsync call that used to sit here, and it takes the surface out of the
	// cross-tenant probe's blind spot: the probe already aimed the victim's projectKey at it and could
	// not know whether anything read the field.
	[TenantFrom(TenantSource.BodyField, "projectKey")]
	static async Task<IResult> CreateShareAsync(
		HttpContext ctx,
		IShareLinkDirectory shareLinks,
		ShareCreateRequest req,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(req.ProjectKey) || string.IsNullOrWhiteSpace(req.Kql))
			return Results.BadRequest(new ErrorResponse("ProjectKey and Kql required"));

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

		await shareLinks.CreateAsync(entity, ct);
		return Results.Ok(new ShareCreatedResponse(id, entity.ExpiresAt));
	}

	// Revoke (spec `share-link-revocable`): "explicit action by someone entitled to mint the same
	// token, independent of TTL". `[TenantFrom(BodyField, "projectKey")]` proves the CALLER is
	// authorized for the project they claim — the exact same proof CreateShareAsync requires to mint a
	// link in the first place, so only someone who could issue an equivalent token can revoke this one.
	//
	// That proof alone is NOT enough, though — it establishes the caller owns SOME project, not that
	// they own THIS share's project. A caller honestly declaring their own (authorized) projectKey
	// would sail past the PEP above even when aimed at a token that belongs to someone else's project;
	// closing that second half is IShareLinkDirectory.DeleteAsync's job — it matches the row on
	// (token, projectKey) TOGETHER, so a foreign token simply is not found. "Not found" and "wrong
	// tenant" collapse to the identical response on purpose: the alternative (say the projectKey is the
	// mismatch) would let a caller enumerate which tokens exist under a project they cannot reach — a
	// cross-tenant existence oracle. This also happens to be the SAME shape neighbouring deletes already
	// answer with (DataDbsApi.DeleteAsync, ConfigApi.Delete, AgentKeyAdminService.RevokeAsync) — a
	// deliberate reuse of the tree's one way to answer "no such row here", not a new one.
	//
	// Hard delete, not soft: the row must stop being READABLE immediately, and there is no second
	// consumer of a "revoked but still present" ShareLinks row anywhere in the tree — a soft-delete flag
	// would just be one more place GetTsvAsync/Share.cshtml.cs could forget to check.
	[TenantFrom(TenantSource.BodyField, "projectKey")]
	static async Task<IResult> DeleteShareAsync(
		IShareLinkDirectory shareLinks,
		string token,
		[FromBody] ShareDeleteRequest req,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(req.ProjectKey))
			return Results.BadRequest(new ErrorResponse("ProjectKey required"));

		var deleted = await shareLinks.DeleteAsync(token, req.ProjectKey, ct);
		return deleted
			? Results.Ok(new DeletedResponse(true))
			: Results.NotFound(new ErrorResponse("share link not found"));
	}

	// `capability-token` in its textbook form, and the one surface in the tree the class was written for:
	// the share TOKEN is the authorization. It was issued by an explicit act (somebody with access to the
	// project called CreateShareAsync above), it carries the entire extent of access with it — the stored
	// row names the project, the log, the KQL, the columns and the masking policy — and the caller
	// presents nothing else, which is why the route is AllowAnonymous. Per the spec this class does NOT
	// cover a token that merely identifies a caller; nobody is identified here.
	[TenantExempt(TenantExemption.CapabilityToken,
		"the share token IS the grant: issued by an explicit act, and the stored link — not the caller — "
		+ "names the project, log, query, columns and masking of exactly what may be read")]
	static async Task<IResult> GetTsvAsync(
		HttpContext ctx,
		IShareLinkDirectory shareLinks,
		ILogStore store,
		string token,
		CancellationToken ct)
	{
		var share = await shareLinks.FindAsync(token, ct);
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

		using var logDb = store.NewEnsuredContext(share.ProjectKey, share.LogName);
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
