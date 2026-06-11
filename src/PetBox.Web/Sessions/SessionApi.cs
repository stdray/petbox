using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Sessions.Contract;

namespace PetBox.Web.Sessions;

// Non-MCP session push, for the Claude Code Stop hook (a shell command can't easily speak MCP).
// The body is ndjson — one JSON message {role, content} per line, in order. The server numbers
// the messages (ordinal) and stores the latest snapshot; last-write-wins (the hook re-pushes the
// full transcript each turn, so the snapshot is always a superset). Mirrors session.upsert.
public static class SessionApi
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/sessions/{projectKey}/{sessionId}", UpsertAsync)
			.Accepts<string>("application/x-ndjson")
			.Produces<SessionUpsertResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("ApiKey");

		app.MapDelete("/api/sessions/{projectKey}/{sessionId}", DeleteAsync)
			.Produces<DeletedResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");

		// Headers only (id, agent, version, updated) — the upgrade-only guard of the
		// history importer compares its local message count against `version` before
		// pushing, so a stale file read can't roll back a fresher snapshot.
		app.MapGet("/api/sessions/{projectKey}", ListAsync)
			.Produces<SessionListResponse>()
			.RequireAuthorization("ApiKey");
	}

	static async Task<IResult> ListAsync(
		HttpContext ctx, string projectKey, ISessionService sessions, CancellationToken ct)
	{
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
			return TypedResults.Forbid();
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Contains("tasks:read"))
			return TypedResults.Forbid();

		var list = await sessions.ListAsync(projectKey, ct);
		return TypedResults.Ok(new SessionListResponse(
			list.Select(s => new SessionHeaderResponse(s.SessionId, s.Agent, s.Version, s.Updated)).ToList()));
	}

	static async Task<IResult> UpsertAsync(
		HttpContext ctx, string projectKey, string sessionId, ISessionService sessions, CancellationToken ct)
	{
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
			return TypedResults.Forbid();
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Contains("tasks:write"))
			return TypedResults.Forbid();

		var agent = ctx.Request.Query["agent"].FirstOrDefault() ?? "claude-code";

		var messages = new List<SessionMessageInput>();
		using var reader = new StreamReader(ctx.Request.Body);
		string? line;
		while ((line = await reader.ReadLineAsync(ct)) is not null)
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			SessionMessageInput? m;
			try { m = JsonSerializer.Deserialize<SessionMessageInput>(line, Json); }
			catch (JsonException) { return TypedResults.BadRequest(new ErrorResponse("invalid ndjson line")); }
			if (m is null || string.IsNullOrEmpty(m.Content)) continue;
			messages.Add(m.Role is null ? m with { Role = "" } : m);
		}
		if (messages.Count == 0)
			return TypedResults.BadRequest(new ErrorResponse("empty body"));

		var o = await sessions.UpsertAsync(projectKey, sessionId, agent, messages, ct);
		return TypedResults.Ok(new SessionUpsertResponse(o.SessionId, o.Version, o.MessageCount));
	}

	// Soft delete; a later push of the same sessionId resurrects it. Mirrors session.delete.
	static async Task<IResult> DeleteAsync(
		HttpContext ctx, string projectKey, string sessionId, ISessionService sessions, CancellationToken ct)
	{
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
			return TypedResults.Forbid();
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Contains("tasks:write"))
			return TypedResults.Forbid();

		return await sessions.DeleteAsync(projectKey, sessionId, ct)
			? TypedResults.Ok(new DeletedResponse(true))
			: TypedResults.NotFound(new ErrorResponse("session not found"));
	}
}
