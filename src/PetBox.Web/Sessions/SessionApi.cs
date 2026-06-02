using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Data.Temporal;
using PetBox.Sessions.Data;

namespace PetBox.Web.Sessions;

// Non-MCP session push, for the Claude Code Stop hook (a shell command can't easily
// speak MCP). POST the plan/session blob; last-write-wins (reads the current version
// as the baseline so repeated per-turn pushes never conflict). Mirrors session.append.
public static class SessionApi
{
	public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/sessions/{projectKey}/{sessionId}", AppendAsync).RequireAuthorization("ApiKey");
	}

	static async Task<IResult> AppendAsync(
		HttpContext ctx, string projectKey, string sessionId, ISessionStore sessions, CancellationToken ct)
	{
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
			return Results.Forbid();
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Contains("tasks:write"))
			return Results.Forbid();

		var agent = ctx.Request.Query["agent"].FirstOrDefault() ?? "claude-code";
		using var reader = new StreamReader(ctx.Request.Body);
		var content = await reader.ReadToEndAsync(ct);
		if (string.IsNullOrWhiteSpace(content))
			return Results.BadRequest(new { error = "empty body" });

		var current = await sessions.GetAsync(projectKey, sessionId, ct);
		var db = sessions.GetContext(projectKey);
		var r = await TemporalStore.UpsertAsync(db, new[]
		{
			new SessionRow { Key = sessionId, Version = current?.Version ?? 0, Agent = agent, Content = content },
		}, ct: ct);
		return Results.Ok(new { applied = r.Applied, currentVersion = r.CurrentVersion });
	}
}
