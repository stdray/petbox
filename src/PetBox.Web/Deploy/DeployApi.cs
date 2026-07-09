using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Config;
using PetBox.Config.Contract;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Deploy.Contract;

namespace PetBox.Web.Deploy;

// REST surface of the deploy control-plane:
//  - /agent/* is the node-agent pull contract (node-scoped keys: agent:poll / agent:heartbeat).
//  - /api/deploy/nodes onboards a node and mints its node-scoped key (deploy:write).
// Like HealthApi, endpoints RequireAuthorization("ApiKey") then assert the scope manually.
public static class DeployApi
{
	// Scopes a freshly-minted node key carries: poll desired state, report heartbeat, ship
	// container logs. NO config:read — env is resolved server-side at poll (see PollAsync).
	const string NodeKeyScopes = ApiKeyScopes.AgentPoll + "," + ApiKeyScopes.AgentHeartbeat + "," + ApiKeyScopes.LogsIngest;

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

	static async Task<IResult> PollAsync(HttpContext ctx, IDeployService svc, PetBoxDb db, IConfigService configService, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.AgentPoll)) return Results.Forbid();
		var nodeId = Claim(ctx, "project");
		if (string.IsNullOrWhiteSpace(nodeId)) return TypedResults.BadRequest(new ErrorResponse("node key has no node claim"));

		var poll = await svc.PollAsync(nodeId, ct);
		// Resolve each deployment's env server-side (config-resolve over (Project, ConfigTags))
		// so the node key needs no config:read and there is no project-claim mismatch.
		var enriched = new List<PollItem>(poll.Deployments.Count);
		foreach (var d in poll.Deployments)
			enriched.Add(d with { Env = await ResolveEnvAsync(db, configService, d.Project, d.ConfigTags, ct) });
		return TypedResults.Ok(poll with { Deployments = enriched });
	}

	// Reuses the config-resolve pipeline (same as GET /v1/conf) to produce the container env
	// for one deployment. Returns empty on unknown project or ambiguous config.
	static async Task<Dictionary<string, string>> ResolveEnvAsync(
		PetBoxDb db, IConfigService configService, string project, string configTags, CancellationToken ct)
	{
		var proj = db.Projects.FirstOrDefault(p => p.Key == project);
		if (proj is null) return new Dictionary<string, string>(StringComparer.Ordinal);

		var tags = new List<string> { $"ws:{proj.WorkspaceKey}", $"project:{project}" };
		tags.AddRange(configTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

		try
		{
			var resolved = await configService.ResolveAsync(proj.WorkspaceKey, tags, ct);
			return new Dictionary<string, string>(resolved, StringComparer.Ordinal);
		}
		catch (AmbiguousConfigException) { return new Dictionary<string, string>(StringComparer.Ordinal); }
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

	static async Task<IResult> EnrollNodeAsync(HttpContext ctx, IDeployService svc, PetBoxDb db, NodeEnrollRequest req, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.DeployWrite)) return Results.Forbid();
		if (req is null || string.IsNullOrWhiteSpace(req.Id))
			return TypedResults.BadRequest(new ErrorResponse("id is required"));

		var keyRef = $"node:{req.Id.Trim().ToLowerInvariant()}";
		var node = await svc.UpsertNodeAsync(new NodeInput(
			req.Id, req.DisplayName ?? req.Id, req.Tags ?? "", req.Ephemeral, KeyRef: req.MintKey ? keyRef : null), ct);

		string? key = null;
		if (req.MintKey)
		{
			// One live node key per node: drop any previous one with this KeyRef name, mint fresh.
			await db.ApiKeys.Where(k => k.Name == keyRef).DeleteAsync(ct);
			key = $"yb_key_node_{Guid.NewGuid():N}";
			await db.InsertAsync(new ApiKey
			{
				Key = key,
				ProjectKey = node.Id,        // the node id is the agent's "project" claim
				Scopes = NodeKeyScopes,
				Name = keyRef,
				CreatedAt = DateTime.UtcNow,
			}, token: ct);
		}
		return TypedResults.Ok(new NodeEnrollResponse(node, key));
	}

	static string? Claim(HttpContext ctx, string type) =>
		ctx.User.Claims.FirstOrDefault(c => c.Type == type)?.Value;

	static bool HasScope(HttpContext ctx, string scope) =>
		(Claim(ctx, "scopes") ?? "")
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Contains(scope, StringComparer.Ordinal);
}
