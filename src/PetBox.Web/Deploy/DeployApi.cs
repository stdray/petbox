using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Deploy.Contract;

namespace PetBox.Web.Deploy;

// REST surface of the deploy control-plane:
//  - /agent/* is the node-agent pull contract (node-scoped keys: agent:poll / agent:heartbeat).
//  - /api/deploy/nodes onboards a node and mints its node-scoped key (deploy:write).
// Like HealthApi, endpoints RequireAuthorization("ApiKey") then assert the scope manually.
//
// These handlers open NO database. Poll's server-side env resolution and node enrollment both need
// core.db, the config db and the deploy db at once; they live in IDeployAgentService, because a
// minimal-API lambda is pipeline code and the database is visible only in the service layer
// (AGENTS.md). What is left here is exactly what an endpoint is for: scope check, shape check, and
// the mapping between the wire and the domain.
public static class DeployApi
{
	public static void MapDeployEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/agent/poll", PollAsync)
			.Produces<PollResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("ApiKey");
		app.MapPost("/agent/heartbeat", HeartbeatAsync)
			.Produces<OkResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("ApiKey");
		app.MapPost("/api/deploy/nodes", EnrollNodeAsync)
			.Produces<NodeEnrollResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("ApiKey");
	}

	static async Task<IResult> PollAsync(HttpContext ctx, IDeployAgentService agents, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.AgentPoll)) return Results.Forbid();
		var nodeId = Claim(ctx, "project");
		if (string.IsNullOrWhiteSpace(nodeId)) return TypedResults.BadRequest(new ErrorResponse("node key has no node claim"));

		return TypedResults.Ok(await agents.PollAsync(nodeId, ct));
	}

	static async Task<IResult> HeartbeatAsync(HttpContext ctx, IDeployService svc, HeartbeatReport req, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.AgentHeartbeat)) return Results.Forbid();
		var nodeId = Claim(ctx, "project");
		if (string.IsNullOrWhiteSpace(nodeId)) return TypedResults.BadRequest(new ErrorResponse("node key has no node claim"));
		await svc.ApplyHeartbeatAsync(nodeId, req ?? new HeartbeatReport([]), ct);
		return TypedResults.Ok(new OkResponse(true));
	}

	public sealed record NodeEnrollRequest(string Id, string? DisplayName, string? Tags, bool Ephemeral, bool MintKey);
	public sealed record NodeEnrollResponse(NodeView Node, string? Key);

	static async Task<IResult> EnrollNodeAsync(HttpContext ctx, IDeployAgentService agents, NodeEnrollRequest req, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.DeployWrite)) return Results.Forbid();
		if (req is null || string.IsNullOrWhiteSpace(req.Id))
			return TypedResults.BadRequest(new ErrorResponse("id is required"));

		var enrolled = await agents.EnrollNodeAsync(
			new NodeEnrollInput(req.Id, req.DisplayName, req.Tags, req.Ephemeral, req.MintKey), ct);

		return TypedResults.Ok(new NodeEnrollResponse(enrolled.Node, enrolled.Key));
	}

	static string? Claim(HttpContext ctx, string type) =>
		ctx.User.Claims.FirstOrDefault(c => c.Type == type)?.Value;

	static bool HasScope(HttpContext ctx, string scope) =>
		(Claim(ctx, "scopes") ?? "")
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Contains(scope, StringComparer.Ordinal);
}
