using LinqToDB;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Deploy.Contract;

namespace PetBox.Web.Deploy;

// What POST /api/deploy/nodes asks for, once the endpoint has validated it: a node to onboard and
// whether to mint it a key.
public sealed record NodeEnrollInput(string Id, string? DisplayName, string? Tags, bool Ephemeral, bool MintKey);

// The onboarded node, plus the freshly-minted key — which is returned exactly ONCE, here, because
// nothing ever reads it back out for display.
public sealed record NodeEnrollment(NodeView Node, string? Key);

// The node-agent's server-side half: the two operations behind /agent/poll and /api/deploy/nodes
// that need a DATABASE and therefore cannot live in an endpoint lambda (AGENTS.md — the database is
// visible only in the service layer; an endpoint lambda asks a service, it does not call .Open()).
//
// Both operations straddle THREE stores, which is precisely why they belong in one service rather
// than smeared across a handler: the deploy db (via IDeployService), core.db (the Projects table for
// a deployment's workspace, the ApiKeys table for the node key) and the per-workspace config db.
// /agent/heartbeat is NOT here — it touches only the deploy db, so the endpoint already just calls
// IDeployService and has nothing to hide.
public interface IDeployAgentService
{
	// The node's desired state, with each deployment's container env RESOLVED SERVER-SIDE. The
	// resolution is done here, not by the agent, so a node key needs no config:read scope and there
	// is no project-claim mismatch to reconcile — the node is handed values, never the authority to
	// go and read them.
	Task<PollResponse> PollAsync(string nodeId, CancellationToken ct = default);

	// Onboard (create or update) a node and, if asked, mint its node-scoped key.
	Task<NodeEnrollment> EnrollNodeAsync(NodeEnrollInput input, CancellationToken ct = default);
}

public sealed class DeployAgentService(
	IDeployService deploy,
	ICoreDbFactory dbf,
	IConfigDbFactory configFactory,
	ISecretEncryptor encryptor) : IDeployAgentService
{
	// The scopes a freshly-minted node key carries: poll desired state, report heartbeat, ship
	// container logs. NO config:read — env is resolved server-side in PollAsync, so the node never
	// needs to be able to read config for itself.
	const string NodeKeyScopes =
		ApiKeyScopes.AgentPoll + "," + ApiKeyScopes.AgentHeartbeat + "," + ApiKeyScopes.LogsIngest;

	public async Task<PollResponse> PollAsync(string nodeId, CancellationToken ct = default)
	{
		var poll = await deploy.PollAsync(nodeId, ct);

		// ONE core connection for the whole poll, reused across every deployment in it — not one per
		// deployment. (A linq2db DataConnection is not thread-safe, but this loop is sequential and
		// the connection is call-owned, so reuse here is safe and is the cheap shape.)
		using var db = dbf.Open();
		var enriched = poll.Deployments
			.Select(d => d with { Env = ResolveEnv(db, d.Project, d.ConfigTags) })
			.ToList();

		return poll with { Deployments = enriched };
	}

	public async Task<NodeEnrollment> EnrollNodeAsync(NodeEnrollInput input, CancellationToken ct = default)
	{
		var keyRef = $"node:{input.Id.Trim().ToLowerInvariant()}";

		var node = await deploy.UpsertNodeAsync(new NodeInput(
			input.Id,
			input.DisplayName ?? input.Id,
			input.Tags ?? "",
			input.Ephemeral,
			KeyRef: input.MintKey ? keyRef : null), ct);

		if (!input.MintKey) return new NodeEnrollment(node, null);

		using var db = dbf.Open();

		// One live node key per node: drop any previous one with this KeyRef name, mint fresh.
		await db.ApiKeys.Where(k => k.Name == keyRef).DeleteAsync(ct);

		var key = $"yb_key_node_{Guid.NewGuid():N}";
		await db.InsertAsync(new ApiKey
		{
			Key = key,
			ProjectKey = node.Id,        // the node id is the agent's "project" claim
			Scopes = NodeKeyScopes,
			Name = keyRef,
			CreatedAt = DateTime.UtcNow,
		}, token: ct);

		return new NodeEnrollment(node, key);
	}

	// Reuses the config-resolve pipeline (the same one behind GET /v1/conf) to produce the container
	// env for one deployment. Returns empty on an unknown project or ambiguous config.
	Dictionary<string, string> ResolveEnv(PetBoxDb db, string project, string configTags)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		var proj = db.Projects.FirstOrDefault(p => p.Key == project);
		if (proj is null) return result;

		var tags = new List<string> { $"ws:{proj.WorkspaceKey}", $"project:{project}" };
		tags.AddRange(configTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

		using var configDb = configFactory.NewConfigDb(proj.WorkspaceKey);
		var bindings = configDb.Bindings.ToList();
		try
		{
			foreach (var m in ResolvePipeline.ResolveAll(tags, bindings))
				result[m.Binding.Path] = ResolveValue(m.Binding);
		}
		catch (AmbiguousConfigException) { /* leave whatever resolved; ambiguity is a config bug to fix in UI */ }
		return result;
	}

	string ResolveValue(ConfigBinding b)
	{
		if (b.Kind == BindingKind.Secret && encryptor.IsAvailable
			&& b.Ciphertext is not null && b.Iv is not null && b.AuthTag is not null)
		{
			try { return encryptor.Decrypt(b.Ciphertext, b.Iv, b.AuthTag); }
			catch { return string.Empty; }
		}
		return b.Value;
	}
}
