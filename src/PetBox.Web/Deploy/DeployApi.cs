using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Deploy.Contract;
using PetBox.Web.Contract;

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
//
// ALL THREE ARE `fleet-wide`, DECLARED DELIBERATELY — this is the class the deploy control plane was
// named for, and the reason is visible in the code rather than assumed:
//
//   * These endpoints are addressed by NODE, never by tenant. There is no {projectKey} and no
//     {workspaceKey} in any of the three routes, and no tenant in any body. Poll and Heartbeat read
//     the node id out of the `host` claim — its OWN carrier since M050 (spec node-grant-own-carrier);
//     it used to be read out of `project`, which meant the deploy plane and the tenant axis shared
//     one claim slot and a node named like a project was the same value to both readers. A
//     project-shaped check here would still mean nothing, because a node key now carries an EMPTY
//     project claim, and a blank claim authorizes no project (ProjectScope.Authorizes). A
//     `[TenantFrom(CallerDefault)]` would be actively wrong for the same reason it always was.
//   * The enrol route mints a node-scoped key and needs `deploy:write`, which ApiKeyScopes itself
//     documents as NEAR-ROOT, FLEET-WIDE. The MCP deploy_* verbs declared the same class in the MCP
//     wave for the same reason (nine of them), and the cross-tenant probe measured the effect rather
//     than taking anyone's word for it: deploy_upsert attaches a deployment to ANY tenant's project
//     with `deploy:write` alone, which is recorded as a KnownDeviations entry, not hidden by the
//     exemption.
//
// So the exemption is a statement that the tenant axis does not apply to this plane, not a way past
// it. What still applies, unchanged, is the SCOPE axis: agent:poll, agent:heartbeat, deploy:write are
// checked below and an exemption says nothing about them. The narrowing of `deploy:write` is a
// separate, already-documented concern (work `deploy-tools-fleet-wide-undocumented`).
public static class DeployApi
{
	const string FleetWideReason =
		"the deploy control plane is addressed by node id, not by tenant: no route or body here names a "
		+ "project or a workspace, and a node-scoped key carries its node in the `host` claim with an "
		+ "empty `project` claim";

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

	[TenantExempt(TenantExemption.FleetWide, FleetWideReason)]
	static async Task<IResult> PollAsync(HttpContext ctx, IDeployAgentService agents, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.AgentPoll)) return Results.Forbid();
		// The `host` claim, NOT `project`: a key that names a project is not a node, and since M050
		// it cannot pretend to be one by being named after it.
		var nodeId = Claim(ctx, ApiKeyAuthenticationHandler.HostClaim);
		if (string.IsNullOrWhiteSpace(nodeId)) return TypedResults.BadRequest(new ErrorResponse("node key has no host claim"));

		return TypedResults.Ok(await agents.PollAsync(nodeId, ct));
	}

	[TenantExempt(TenantExemption.FleetWide, FleetWideReason)]
	static async Task<IResult> HeartbeatAsync(HttpContext ctx, IDeployService svc, HeartbeatReport req, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.AgentHeartbeat)) return Results.Forbid();
		var nodeId = Claim(ctx, ApiKeyAuthenticationHandler.HostClaim);
		if (string.IsNullOrWhiteSpace(nodeId)) return TypedResults.BadRequest(new ErrorResponse("node key has no host claim"));
		await svc.ApplyHeartbeatAsync(nodeId, req ?? new HeartbeatReport([]), ct);
		return TypedResults.Ok(new OkResponse(true));
	}

	// NodeEnrollRequest/NodeEnrollResponse (the /api/deploy/nodes wire contract) moved to
	// Web/Contract/WebResponses.cs (resharper-clt-move-wire-records) — that directory's glob
	// already covers the NotAccessedPositionalProperty.Global finding a point [PublicAPI] used to
	// carry here.

	[TenantExempt(TenantExemption.FleetWide, FleetWideReason)]
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
		ApiKeyScopes.Granted(ctx.User, scope);
}
