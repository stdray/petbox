using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
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
		app.MapGet("/agent/poll", PollAsync).RequireAuthorization("ApiKey");
		app.MapPost("/agent/heartbeat", HeartbeatAsync).RequireAuthorization("ApiKey");
		app.MapPost("/api/deploy/nodes", EnrollNodeAsync).RequireAuthorization("ApiKey");
	}

	static async Task<IResult> PollAsync(HttpContext ctx, IDeployService svc, PetBoxDb db, IConfigDbFactory configFactory, ISecretEncryptor encryptor, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.AgentPoll)) return Results.Forbid();
		var nodeId = Claim(ctx, "project");
		if (string.IsNullOrWhiteSpace(nodeId)) return Results.BadRequest(new { error = "node key has no node claim" });

		var poll = await svc.PollAsync(nodeId, ct);
		// Resolve each deployment's env server-side (config-resolve over (Project, ConfigTags))
		// so the node key needs no config:read and there is no project-claim mismatch.
		var enriched = poll.Deployments
			.Select(d => d with { Env = ResolveEnv(db, configFactory, encryptor, d.Project, d.ConfigTags) })
			.ToList();
		return Results.Ok(poll with { Deployments = enriched });
	}

	// Reuses the config-resolve pipeline (same as GET /v1/conf) to produce the container env
	// for one deployment. Returns empty on unknown project or ambiguous config.
	static Dictionary<string, string> ResolveEnv(
		PetBoxDb db, IConfigDbFactory configFactory, ISecretEncryptor encryptor, string project, string configTags)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		var proj = db.Projects.FirstOrDefault(p => p.Key == project);
		if (proj is null) return result;

		var tags = new List<string> { $"ws:{proj.WorkspaceKey}", $"project:{project}" };
		tags.AddRange(configTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

		var bindings = configFactory.GetConfigDb(proj.WorkspaceKey).Bindings.ToList();
		try
		{
			foreach (var m in ResolvePipeline.ResolveAll(tags, bindings))
				result[m.Binding.Path] = ResolveValue(m.Binding, encryptor);
		}
		catch (AmbiguousConfigException) { /* leave whatever resolved; ambiguity is a config bug to fix in UI */ }
		return result;
	}

	static string ResolveValue(ConfigBinding b, ISecretEncryptor encryptor)
	{
		if (b.Kind == BindingKind.Secret && encryptor.IsAvailable
			&& b.Ciphertext is not null && b.Iv is not null && b.AuthTag is not null)
		{
			try { return encryptor.Decrypt(b.Ciphertext, b.Iv, b.AuthTag); }
			catch { return string.Empty; }
		}
		return b.Value;
	}

	static async Task<IResult> HeartbeatAsync(HttpContext ctx, IDeployService svc, HeartbeatReport req, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.AgentHeartbeat)) return Results.Forbid();
		var nodeId = Claim(ctx, "project");
		if (string.IsNullOrWhiteSpace(nodeId)) return Results.BadRequest(new { error = "node key has no node claim" });
		await svc.ApplyHeartbeatAsync(nodeId, req ?? new HeartbeatReport([]), ct);
		return Results.Ok(new { ok = true });
	}

	public sealed record NodeEnrollRequest(string Id, string? DisplayName, string? Tags, bool Ephemeral, bool MintKey);
	public sealed record NodeEnrollResponse(NodeView Node, string? Key);

	static async Task<IResult> EnrollNodeAsync(HttpContext ctx, IDeployService svc, PetBoxDb db, NodeEnrollRequest req, CancellationToken ct)
	{
		if (!HasScope(ctx, ApiKeyScopes.DeployWrite)) return Results.Forbid();
		if (req is null || string.IsNullOrWhiteSpace(req.Id))
			return Results.BadRequest(new { error = "id is required" });

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
		return Results.Ok(new NodeEnrollResponse(node, key));
	}

	static string? Claim(HttpContext ctx, string type) =>
		ctx.User.Claims.FirstOrDefault(c => c.Type == type)?.Value;

	static bool HasScope(HttpContext ctx, string scope) =>
		(Claim(ctx, "scopes") ?? "")
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Contains(scope, StringComparer.Ordinal);
}
