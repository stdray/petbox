using System.Text;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;

namespace PetBox.Web.Blobs;

// THE TRANSPORT half of work/write-body-by-reference (spec no-retransmission-of-existing-content).
//
//   POST /api/blobs/{projectKey}   raw request body -> { ref, bytes, chars, expiresAt }
//
// WHY REST AND NOT AN MCP VERB — the load-bearing decision of the whole card, so it is written down
// rather than assumed. An MCP verb takes its arguments as JSON, and a JSON string argument is
// exactly where the cost being removed lives: the body would still pass through the model's OUTPUT
// budget, and a client that escapes non-ASCII as \uXXXX would still spend six output characters per
// Cyrillic character on the way. A verb would be a second way to TYPE the text; what the card
// demands is a way to move a file that ALREADY EXISTS. So the payload is the raw request body — no
// envelope, no field, nothing to escape — and the MCP call carries a 37-character reference.
//
// Conventions follow MemoryApi.MapMemoryEndpoints, the other non-MCP surface built for agent
// tooling: RequireAuthorization("ApiKey"), an explicit scope assertion inside the handler, and a
// [TenantFrom] declaration so the PEP (TenantEnforcementMiddleware) authorizes the route project —
// sandbox containment included — before the handler is reached at all.
public static class BodyRefApi
{
	public static void MapBodyRefEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/blobs/{projectKey}", UploadAsync)
			.Produces<BodyRefUploadResponse>()
			.RequireAuthorization("ApiKey");
	}

	// TENANT: the project in the route. A blob's tenant is the project it is uploaded INTO — the
	// same key the substitution path later matches on — so the route value is the whole of it. There
	// is no DERIVED second container here of the kind SandboxContainment exists to catch, which is
	// why this file names no container vocabulary and is not a call site of that predicate:
	// containment for this surface is applied by the PEP, to the one tenant this route names.
	//
	// SCOPE: no new `blobs:write` scope is minted, and that is a decision on evidence rather than
	// taste. ApiKeyScopes.Granted is exact set membership — no wildcard, no hierarchy, no prefix
	// rule — so a newly minted scope is absent from every ApiKey that already exists, the smoke key
	// included, and the feature could not be exercised on the live stand after deploy until the
	// owner re-minted keys. A capability that cannot be verified where it runs is worse than one
	// gated slightly coarsely. The gate used instead is not lax: uploading is only ever a PREFIX of
	// writing, so requiring that the caller could already write SOMETHING into this project
	// (tasks:write or memory:write — between them they gate every verb `bodyRef` is offered on)
	// grants no reach a plain tasks_upsert did not already grant. A read-only key still cannot
	// upload; a key that can write to project A still cannot upload into project B.
	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> UploadAsync(
		HttpContext ctx, string projectKey, IBodyRefBlobStore blobs, CancellationToken ct)
	{
		if (!ApiKeyScopes.Granted(ctx.User, ApiKeyScopes.TasksWrite)
			&& !ApiKeyScopes.Granted(ctx.User, ApiKeyScopes.MemoryWrite))
			return TypedResults.Forbid();

		// THE CHEAPEST REFUSAL FIRST: a declared Content-Length over the ceiling is refused without
		// reading a single byte of the body — which is the case for every ordinary file upload, since
		// a client posting a file knows its length. A chunked upload declares none; that is what the
		// bounded read below is for, and it is not merely a belt on this brace.
		if (ctx.Request.ContentLength is { } declared && declared > BodyRefs.MaxBytes)
			return TooLarge(declared);

		var (bytes, overLimit) = await ReadBoundedAsync(ctx.Request.Body, BodyRefs.MaxBytes, ct);
		if (overLimit)
			return TooLarge(null);
		if (bytes.Length == 0)
			return TypedResults.BadRequest(new ErrorResponse(
				"The request body is empty. POST the file's bytes as the raw body (no JSON envelope, no "
				+ "multipart form) — that rawness is the point: a JSON argument would reintroduce the "
				+ "escaping cost this endpoint exists to remove."));

		string body;
		try
		{
			body = Decode(bytes);
		}
		catch (DecoderFallbackException)
		{
			// Refused rather than replacement-charactered. A blob becomes a body VERBATIM; silently
			// substituting U+FFFD would store corruption nobody can later attribute to this hop — the
			// exact failure mode (mojibake with no author) the card set out to end.
			return TypedResults.BadRequest(new ErrorResponse(
				"The body is not valid UTF-8. This endpoint carries TEXT (it becomes a node/entry/comment "
				+ "body verbatim), so it decodes strictly and refuses rather than storing replacement "
				+ "characters. Re-encode the file as UTF-8 and upload it again."));
		}

		var blob = BodyRefBlobStore.NewBlob(
			projectKey, body, bytes.Length,
			ctx.User.FindFirst(ApiKeyAuthenticationHandler.KeyNameClaim)?.Value ?? "", DateTime.UtcNow);
		await blobs.PutAsync(blob, ct);

		// `chars` beside `bytes` is not decoration: it is the one number that lets a caller verify
		// nothing was lost in the hop, and for a Cyrillic body the two differ by about 2x — which is
		// itself half of the measurement this card is about.
		return TypedResults.Ok(new BodyRefUploadResponse(blob.Ref, blob.Bytes, body.Length, blob.ExpiresAt));
	}

	static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ErrorResponse> TooLarge(long? declared) =>
		TypedResults.Json(
			new ErrorResponse(
				$"Body is larger than the {BodyRefs.MaxBytes / (1024 * 1024)} MB blob ceiling"
				+ (declared is { } d ? $" (Content-Length {d})" : "")
				+ ". This is a transport for a body somebody will later READ into a context window, not "
				+ "an archive — split the file, or upload only the part that belongs in the node."),
			statusCode: StatusCodes.Status413PayloadTooLarge);

	// A BOUNDED read: at most `max` + 1 bytes are ever held. That one extra byte is the whole trick —
	// it is how "exactly at the ceiling" is told apart from "over it" without buffering the overage.
	// A 2 GB POST is refused once max+1 bytes have been seen, not after 2 GB have been copied into
	// this process's memory. (Kestrel's own default MaxRequestBodySize, ~30 MB, is a far looser
	// backstop above this and is deliberately left alone: it is a host-wide setting, and the ceiling
	// that matters here belongs to this endpoint.)
	static async Task<(byte[] Bytes, bool OverLimit)> ReadBoundedAsync(Stream source, long max, CancellationToken ct)
	{
		using var buffer = new MemoryStream();
		var chunk = new byte[64 * 1024];
		while (true)
		{
			var n = await source.ReadAsync(chunk, ct);
			if (n == 0) break;
			buffer.Write(chunk, 0, n);
			if (buffer.Length > max) return ([], true);
		}
		return (buffer.ToArray(), false);
	}

	// Strict UTF-8 (throwOnInvalidBytes), and the BOM goes. A file written by a Windows editor
	// routinely starts EF BB BF; left in place that becomes an invisible U+FEFF as the first
	// character of a markdown body, which silently breaks a leading heading and appears in nothing a
	// human looks at. This is the only place that knows the text came from a FILE, so it is the only
	// place that can strip it.
	static string Decode(byte[] bytes)
	{
		var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
			.GetString(bytes);
		return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
	}
}

// POST /api/blobs/{projectKey} -> the reference to hand a write verb's `bodyRef`, plus what the
// server actually received. `expiresAt` is UTC: when an UNCONSUMED blob stops resolving.
public sealed record BodyRefUploadResponse(string Ref, long Bytes, int Chars, DateTime ExpiresAt);
