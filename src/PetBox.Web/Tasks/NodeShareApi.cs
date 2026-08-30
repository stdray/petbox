using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.Web.Tasks;

// Mint a public link onto ONE task node (spec `node-share`). `ttlMinutes` omitted or 0 ⇒ the link
// never expires (spec `node-share-lifetime`) — see NodeShareCreateRequest.
public sealed record NodeShareCreateRequest(
	string ProjectKey,
	string Board,
	string NodeId,
	string Scope,
	string? CommentId = null,
	// NULLABLE and 0-means-forever, which is the OPPOSITE of ShareCreateRequest.TtlMinutes (where a
	// missing/zero ttl falls back to 24h). Deliberate, and the sharpest difference between the two
	// families: a log export is a snapshot somebody pulls once, while a published node is a page
	// somebody puts in a document — a link that dies on its own is the wrong default there, and the
	// owner chose the explicit "no expiry" over "a very long one".
	int? TtlMinutes = null);

// The response ShareCreatedResponse cannot be: its ExpiresAt is a non-nullable DateTime, and
// serializing "never expires" as DateTime.MaxValue (or as 0001-01-01) would be a lie the UI would
// then have to decode. `null` says exactly what is true.
public sealed record NodeShareCreatedResponse(string Id, DateTime? ExpiresAt);

// MINTING ONLY. There is deliberately no revoke route here: a token is opaque, so "which table is
// this in" is the system's question and DELETE /api/share/{token} (plus mcp:share_revoke) answers it
// for BOTH families through IShareRevocationService. One button, one tool — see that service.
//
// There is no public READ route here either: /ui/share/node/{token} is its own work item. The model
// and the directory are shaped for it (the row names project, board, node, comment and scope, so the
// reader needs nothing from the caller but the token), but nothing is published until that page
// exists — a mint endpoint whose links resolve to a 404 is a smaller, more honest gap than a reader
// nobody has reviewed.
public static class NodeShareApi
{
	public static void MapNodeShareEndpoints(this IEndpointRouteBuilder app)
	{
		// "AuthenticatedAnyScheme", exactly as ShareApi.CreateShareAsync: minting is done both from
		// the browser (the node page's Share button, cookie scheme) and by an agent (X-Api-Key), and
		// the default policy was narrowed to cookies to keep api keys off /ui pages. No ApiKeyScope
		// beyond that, again mirroring the log twin — a scope here would make PUBLISHING a node
		// harder than READING it, and the tenant proof is the [TenantFrom] below either way.
		app.MapPost("/api/share/node", CreateAsync)
			.Accepts<NodeShareCreateRequest>("application/json")
			.Produces<NodeShareCreatedResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("AuthenticatedAnyScheme");
	}

	// `projectKey` arrives in the JSON BODY and is fully attacker-controlled; the policy above only
	// proves SOME authenticated identity. Without the tenant proof any authenticated caller could
	// mint a link publishing ANOTHER project's node — the same hole authz-cleanup-phase2-rest closed
	// on POST /api/share, and worse here because a task node carries prose rather than log rows. It
	// is declared, not hand-checked: [TenantFrom(BodyField, "projectKey")], read out of the body by
	// TenantEnforcementMiddleware before this handler runs.
	[TenantFrom(TenantSource.BodyField, "projectKey")]
	static async Task<IResult> CreateAsync(
		HttpContext ctx,
		INodeShareDirectory nodeShares,
		ICommentService comments,
		NodeShareCreateRequest req,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(req.ProjectKey)
			|| string.IsNullOrWhiteSpace(req.Board)
			|| string.IsNullOrWhiteSpace(req.NodeId))
			return Results.BadRequest(new ErrorResponse("ProjectKey, Board and NodeId required"));

		if (!NodeShareScopes.IsValid(req.Scope))
			return Results.BadRequest(new ErrorResponse("Scope must be one of: body, comment, full"));

		// The scope/commentId pair, BOTH ways round. `comment` without an id would mint a link that
		// publishes nothing; `body`/`full` WITH one would store an id the reader is not going to
		// honour — a grant whose stored extent and rendered extent disagree, which is the one thing a
		// capability token must never be.
		var wantsComment = req.Scope == NodeShareScopes.Comment;
		var hasComment = !string.IsNullOrWhiteSpace(req.CommentId);
		if (wantsComment && !hasComment)
			return Results.BadRequest(new ErrorResponse("CommentId required for scope=comment"));
		if (!wantsComment && hasComment)
			return Results.BadRequest(new ErrorResponse("CommentId is only valid for scope=comment"));

		if (wantsComment)
		{
			// The half the tenant PEP cannot see. It proved the caller owns `projectKey`; it did NOT
			// prove that THIS comment hangs under THIS node. Without the check a caller could publish
			// any comment in their own project under an unrelated node's identity — the link would
			// render the wrong node's title above someone else's words, and the mismatch would be
			// invisible to the reader, who has only the token. Resolved through ICommentService (the
			// one door onto comments — TasksBoundaryTests forbids reaching the store), and a comment
			// in ANOTHER project is simply not found here, so this is also project-confined.
			var comment = await comments.GetAsync(req.ProjectKey, req.CommentId!, ct);
			if (comment is null || !string.Equals(comment.NodeId, req.NodeId, StringComparison.OrdinalIgnoreCase))
				return Results.BadRequest(new ErrorResponse("CommentId does not belong to the given node"));
		}

		// Absent or 0 ⇒ null ⇒ never expires. A NEGATIVE ttl is refused rather than folded into
		// "forever": it is far more likely to be an arithmetic slip at the caller than a request for
		// a permanent link, and silently granting the strongest option on a malformed input is how a
		// share link outlives what it was meant to publish.
		if (req.TtlMinutes is < 0)
			return Results.BadRequest(new ErrorResponse("TtlMinutes must be 0 (never expires) or positive"));

		// Same mint as ShareLink.Id: 20 random bytes, hex-lowercase. The token IS the grant, so its
		// only defence is that it cannot be guessed.
		var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
		var now = DateTime.UtcNow;
		DateTime? expiresAt = req.TtlMinutes is > 0 ? now.AddMinutes(req.TtlMinutes.Value) : null;

		var share = new NodeShare
		{
			Id = id,
			ProjectKey = req.ProjectKey,
			Board = req.Board,
			NodeId = req.NodeId,
			CommentId = wantsComment ? req.CommentId : null,
			Scope = req.Scope,
			CreatedAt = now,
			CreatedBy = ctx.User.Identity?.Name ?? "system",
			ExpiresAt = expiresAt,
		};

		await nodeShares.CreateAsync(share, ct);
		return Results.Ok(new NodeShareCreatedResponse(id, expiresAt));
	}
}
