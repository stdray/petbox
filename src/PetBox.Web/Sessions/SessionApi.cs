using System.Text.Json;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Sessions.Contract;

namespace PetBox.Web.Sessions;

// Non-MCP session push, for the Claude Code Stop hook (a shell command can't easily speak MCP).
// The body is ndjson — one JSON message {role, content} per line, in order. Two write shapes:
//   POST /{sessionId}         — full-snapshot upsert, last-write-wins (repair/import path);
//   POST /{sessionId}/append  — incremental against the server-authoritative cursor
//                               (?fromOrdinal=N; the hook's steady-state path).
// Optional header X-PetBox-Session-Meta: raw JSON object string, observed client metadata
// (e.g. role binding stamp) — last-write-wins when present; omitted keeps existing MetaJson.
// The server numbers the messages (ordinal) and stores the latest snapshot either way.
// Mirrors session_upsert / session_append.
//
// TENANT: declared per route ([TenantFrom(Route, "projectKey")]) and enforced by
// TenantEnforcementMiddleware, which is what took the four hand-written ProjectScope calls out. It
// also closed the sharpest hole the cross-tenant probe found on this file: the two POST routes carry
// `.Accepts<string>("application/x-ndjson")`, and a foreign tenant used to be answered 415 straight
// off that metadata — before the handler that would have checked the project ever ran. The tenant
// decision now happens above the endpoint, so the answer is 403 whatever the content type is. The
// tasks:read / tasks:write scope checks are the orthogonal axis and stay.
public static class SessionApi
{
	const string MetaHeader = "X-PetBox-Session-Meta";

	// Default page size for GET /api/sessions/{projectKey} (spec listing-tail-reachable) — a
	// machine caller (the history importer), not a human paging UI, so bigger than the UI
	// listing's PageSize=30 to keep a full-archive walk to a handful of round trips; still
	// header-only (no ContentZ), so a wider page stays cheap.
	const int DefaultListLimit = 200;

	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/sessions/{projectKey}/{sessionId}", UpsertAsync)
			.Accepts<string>("application/x-ndjson")
			.Produces<SessionUpsertResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("ApiKey");

		// Incremental push against the server-authoritative cursor (spec session-append-wire):
		// same ndjson body, plus ?fromOrdinal=N (the ordinal of the FIRST message in the body).
		// Contiguous/overlapping batches apply idempotently (200); a contiguity gap is rejected
		// with a STRUCTURED 409 carrying the server's lastOrdinal so the hook resends the tail.
		app.MapPost("/api/sessions/{projectKey}/{sessionId}/append", AppendAsync)
			.Accepts<string>("application/x-ndjson")
			.Produces<SessionAppendResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<SessionAppendGapResponse>(StatusCodes.Status409Conflict)
			.RequireAuthorization("ApiKey");

		app.MapDelete("/api/sessions/{projectKey}/{sessionId}", DeleteAsync)
			.Produces<DeletedResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");

		// Headers only (id, agent, version, updated, optional meta) — the upgrade-only guard of
		// the history importer compares its local message count against `version` before
		// pushing, so a stale file read can't roll back a fresher snapshot. KEYSET-paged (spec
		// listing-tail-reachable, card listing-keyset-memory-sessions): ?cursor= resumes after
		// the previous call's nextCursor, ?limit= overrides DefaultListLimit. The importer's
		// "compare every session's version" need is served by LOOPING this, never by a single
		// unbounded response — see import-sessions.ts's serverVersions().
		app.MapGet("/api/sessions/{projectKey}", ListAsync)
			.Produces<SessionListResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("ApiKey");
	}

	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> ListAsync(
		HttpContext ctx, string projectKey, ISessionService sessions, CancellationToken ct)
	{
		if (!ApiKeyScopes.Granted(ctx.User, ApiKeyScopes.TasksRead))
			return TypedResults.Forbid();

		var cursor = ctx.Request.Query["cursor"].FirstOrDefault();
		var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) && l > 0 ? l : DefaultListLimit;

		SessionHeaderPage page;
		try
		{
			// No search/agent filter and Updated ascending: the importer just wants every
			// session reached exactly once, in a stable order — which axis doesn't matter to
			// it, only that paging it to the end is possible and honest under concurrent writes.
			page = await sessions.ListPageAsync(projectKey, null, null, SessionSortField.Updated, false, cursor, limit, ct);
		}
		catch (ArgumentException ex)
		{
			// A cursor from a different call shape (or hand-crafted) is refused, not silently
			// restarted under a different ordering — mirrors Upsert/Append's ArgumentException → 400.
			return TypedResults.BadRequest(new ErrorResponse(ex.Message));
		}

		return TypedResults.Ok(new SessionListResponse(
			page.Headers.Select(s => new SessionHeaderResponse(s.SessionId, s.Agent, s.Version, s.Updated, s.MetaJson)).ToList(),
			page.NextCursor));
	}

	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> UpsertAsync(
		HttpContext ctx, string projectKey, string sessionId, ISessionService sessions, CancellationToken ct)
	{
		if (!ApiKeyScopes.Granted(ctx.User, ApiKeyScopes.TasksWrite))
			return TypedResults.Forbid();

		var agent = ctx.Request.Query["agent"].FirstOrDefault() ?? "claude-code";
		var metaJson = ctx.Request.Headers[MetaHeader].FirstOrDefault();

		var (messages, parseError) = await ReadNdjsonAsync(ctx, ct);
		if (parseError is not null)
			return TypedResults.BadRequest(new ErrorResponse(parseError));
		if (messages.Count == 0)
			return TypedResults.BadRequest(new ErrorResponse("empty body"));

		try
		{
			var o = await sessions.UpsertAsync(projectKey, sessionId, agent, messages, metaJson, ct);
			return TypedResults.Ok(new SessionUpsertResponse(o.SessionId, o.Version, o.MessageCount));
		}
		catch (ArgumentException ex)
		{
			return TypedResults.BadRequest(new ErrorResponse(ex.Message));
		}
	}

	// Incremental push: the body is the same ndjson message stream, `fromOrdinal` (query) is the
	// ordinal the batch starts at. Overlap applies idempotently; a gap 409s with the server cursor.
	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> AppendAsync(
		HttpContext ctx, string projectKey, string sessionId, ISessionService sessions, CancellationToken ct)
	{
		if (!ApiKeyScopes.Granted(ctx.User, ApiKeyScopes.TasksWrite))
			return TypedResults.Forbid();

		var agent = ctx.Request.Query["agent"].FirstOrDefault() ?? "claude-code";
		if (!long.TryParse(ctx.Request.Query["fromOrdinal"].FirstOrDefault(), out var fromOrdinal) || fromOrdinal < 1)
			return TypedResults.BadRequest(new ErrorResponse("fromOrdinal (>= 1) query parameter required"));
		var metaJson = ctx.Request.Headers[MetaHeader].FirstOrDefault();

		var (messages, parseError) = await ReadNdjsonAsync(ctx, ct);
		if (parseError is not null)
			return TypedResults.BadRequest(new ErrorResponse(parseError));
		if (messages.Count == 0)
			return TypedResults.BadRequest(new ErrorResponse("empty body"));

		try
		{
			var o = await sessions.AppendAsync(projectKey, sessionId, agent, fromOrdinal, messages, metaJson, ct);
			return o.Applied
				? TypedResults.Ok(new SessionAppendResponse(o.SessionId, o.LastOrdinal, o.Appended))
				: TypedResults.Conflict(new SessionAppendGapResponse("gap", o.LastOrdinal));
		}
		catch (ArgumentException ex)
		{
			return TypedResults.BadRequest(new ErrorResponse(ex.Message));
		}
	}

	// The shared ndjson body reader: one {role, content} JSON object per line, blank lines and
	// content-less messages skipped. Returns (messages, null) or ([], error) on a malformed line.
	static async Task<(List<SessionMessageInput> Messages, string? Error)> ReadNdjsonAsync(
		HttpContext ctx, CancellationToken ct)
	{
		var messages = new List<SessionMessageInput>();
		using var reader = new StreamReader(ctx.Request.Body);
		string? line;
		while ((line = await reader.ReadLineAsync(ct)) is not null)
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			SessionMessageInput? m;
			try { m = JsonSerializer.Deserialize<SessionMessageInput>(line, Json); }
			catch (JsonException) { return ([], "invalid ndjson line"); }
			if (m is null || string.IsNullOrEmpty(m.Content)) continue;
			messages.Add(m.Role is null ? m with { Role = "" } : m);
		}
		return (messages, null);
	}

	// Soft delete; a later push of the same sessionId resurrects it. Mirrors session_delete.
	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> DeleteAsync(
		HttpContext ctx, string projectKey, string sessionId, ISessionService sessions, CancellationToken ct)
	{
		if (!ApiKeyScopes.Granted(ctx.User, ApiKeyScopes.TasksWrite))
			return TypedResults.Forbid();

		return await sessions.DeleteAsync(projectKey, sessionId, ct)
			? TypedResults.Ok(new DeletedResponse(true))
			: TypedResults.NotFound(new ErrorResponse("session not found"));
	}
}
