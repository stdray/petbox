using System.ComponentModel;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Deploy.Contract;
using PetBox.Deploy.Data;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Typed MCP surface for the deploy control-plane. Fleet-wide (no per-project claim);
// reads need deploy:read, writes deploy:write. Mirrors the REST/UI operations: node
// registry + the desired-state grid. Node-agents do NOT use these (they use /agent/*).
[McpServerToolType]
public static class DeployTools
{
	// node-key scopes minted by node_upsert (same as the REST enroll path): poll + heartbeat
	// + ship container logs. No config:read — env is resolved server-side at poll.
	const string NodeKeyScopes = ApiKeyScopes.AgentPoll + "," + ApiKeyScopes.AgentHeartbeat + "," + ApiKeyScopes.LogsIngest;

	[McpServerTool(Name = "deploy.node_list", Title = "List fleet nodes", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DeployNodesResult))]
	[Description("Lists every node in the fleet (id, tags, online, last-seen, deployment count). Requires deploy:read.")]
	public static Task<object> NodeListAsync(IHttpContextAccessor http, IDeployService svc, CancellationToken ct = default) =>
		ModuleMcp.GuardAsync(async () =>
		{
			ModuleMcp.AssertScope(http, ApiKeyScopes.DeployRead);
			return new DeployNodesResult(await svc.ListNodesAsync(ct));
		});

	[McpServerTool(Name = "deploy.node_upsert", Title = "Register/update a node", UseStructuredContent = true, OutputSchemaType = typeof(DeployNodeResult))]
	[Description("Registers or updates a node. With mintKey=true also mints (or rotates) the node-scoped agent key and returns it ONCE. Requires deploy:write.")]
	public static Task<object> NodeUpsertAsync(
		IHttpContextAccessor http, IDeployService svc, PetBoxDb db,
		[Description("Node id (slug), e.g. 'vdsina-1'.")] string id,
		[Description("Display name.")] string? displayName = null,
		[Description("Capability tags CSV, e.g. 'net.x,disk=nvme'.")] string? tags = null,
		[Description("Comes and goes (laptop/WSL2) — failover treats it as relocatable target carefully.")] bool ephemeral = false,
		[Description("Mint (or rotate) the node agent key and return it once.")] bool mintKey = false,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required");

		var keyRef = $"node:{id.Trim().ToLowerInvariant()}";
		var node = await svc.UpsertNodeAsync(new NodeInput(id, displayName ?? id, tags ?? "", ephemeral, mintKey ? keyRef : null), ct);

		string? key = null;
		if (mintKey)
		{
			await db.ApiKeys.Where(k => k.Name == keyRef).DeleteAsync(ct);
			key = $"yb_key_node_{Guid.NewGuid():N}";
			await db.InsertAsync(new ApiKey
			{
				Key = key, ProjectKey = node.Id, Scopes = NodeKeyScopes, Name = keyRef, CreatedAt = DateTime.UtcNow,
			}, token: ct);
		}
		return new DeployNodeResult(node, key);
	});

	[McpServerTool(Name = "deploy.node_delete", Title = "Delete a node", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(DeployDeletedResult))]
	[Description("Deletes a node and cascades its deployments. Requires deploy:write.")]
	public static Task<object> NodeDeleteAsync(
		IHttpContextAccessor http, IDeployService svc,
		[Description("Node id to delete.")] string id,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required");
		return new DeployDeletedResult(await svc.DeleteNodeAsync(id, ct), id);
	});

	[McpServerTool(Name = "deploy.list", Title = "List deployments", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentsResult))]
	[Description("Lists deployments (desired + last actual state), optionally filtered by node and/or service. Requires deploy:read.")]
	public static Task<object> ListAsync(
		IHttpContextAccessor http, IDeployService svc,
		[Description("Filter by node id.")] string? nodeId = null,
		[Description("Filter by service.")] string? service = null,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployRead);
		return new DeployDeploymentsResult(await svc.ListDeploymentsAsync(nodeId, service, ct));
	});

	[McpServerTool(Name = "deploy.upsert", Title = "Create/update a deployment", UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentResult))]
	[Description("Creates (omit id) or updates a deployment of a service on a node. One copy per (service, node). Requires deploy:write.")]
	public static Task<object> UpsertAsync(
		IHttpContextAccessor http, IDeployService svc,
		[Description("Service name (slug).")] string service,
		[Description("Project the service belongs to (its config applies).")] string project,
		[Description("Target node id.")] string nodeId,
		[Description("Image reference/digest to run.")] string imageDigest,
		[Description("Existing deployment id to update; omit to create.")] string? id = null,
		[Description("Desired running (true) or stopped (false).")] bool running = true,
		[Description("Auto-relocate on node failure.")] bool relocatable = false,
		[Description("Tags a node must cover to host this (CSV).")] string? requiredTags = null,
		[Description("Config tag-vector for env resolution (CSV).")] string? configTags = null,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		var d = await svc.UpsertDeploymentAsync(new DeploymentInput(
			id, service, project, nodeId, imageDigest,
			running ? DesiredState.Running : DesiredState.Stopped, relocatable, requiredTags ?? "", configTags ?? ""), ct);
		return new DeployDeploymentResult(d);
	});

	[McpServerTool(Name = "deploy.start", Title = "Start a deployment", UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentResult))]
	[Description("Sets a deployment's desired state to running. Requires deploy:write.")]
	public static Task<object> StartAsync(IHttpContextAccessor http, IDeployService svc,
		[Description("Deployment id.")] string id, CancellationToken ct = default) =>
		SetDesiredAsync(http, svc, id, DesiredState.Running, ct);

	[McpServerTool(Name = "deploy.stop", Title = "Stop a deployment", UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentResult))]
	[Description("Sets a deployment's desired state to stopped. Requires deploy:write.")]
	public static Task<object> StopAsync(IHttpContextAccessor http, IDeployService svc,
		[Description("Deployment id.")] string id, CancellationToken ct = default) =>
		SetDesiredAsync(http, svc, id, DesiredState.Stopped, ct);

	[McpServerTool(Name = "deploy.move", Title = "Move a deployment to another node", UseStructuredContent = true, OutputSchemaType = typeof(DeployDeploymentResult))]
	[Description("Moves a deployment to a different node (the agents reconcile the move). Requires deploy:write.")]
	public static Task<object> MoveAsync(
		IHttpContextAccessor http, IDeployService svc,
		[Description("Deployment id.")] string id,
		[Description("Destination node id.")] string toNodeId,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		if (string.IsNullOrWhiteSpace(toNodeId)) throw new ArgumentException("toNodeId is required");
		var d = await svc.GetDeploymentAsync(id, ct) ?? throw new InvalidOperationException("deployment not found");
		return new DeployDeploymentResult(await svc.UpsertDeploymentAsync(ToInput(d) with { NodeId = toNodeId }, ct));
	});

	[McpServerTool(Name = "deploy.delete", Title = "Delete a deployment", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(DeployDeletedResult))]
	[Description("Deletes a deployment (the owning node's agent then removes the container). Requires deploy:write.")]
	public static Task<object> DeleteAsync(
		IHttpContextAccessor http, IDeployService svc,
		[Description("Deployment id to delete.")] string id,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
		return new DeployDeletedResult(await svc.DeleteDeploymentAsync(id, ct), id);
	});

	static Task<object> SetDesiredAsync(IHttpContextAccessor http, IDeployService svc, string id, DesiredState desired, CancellationToken ct) =>
		ModuleMcp.GuardAsync(async () =>
		{
			ModuleMcp.AssertScope(http, ApiKeyScopes.DeployWrite);
			var d = await svc.GetDeploymentAsync(id, ct) ?? throw new InvalidOperationException("deployment not found");
			return new DeployDeploymentResult(await svc.UpsertDeploymentAsync(ToInput(d) with { DesiredState = desired }, ct));
		});

	static DeploymentInput ToInput(DeploymentView d) => new(
		d.Id, d.Service, d.Project, d.NodeId, d.ImageDigest, d.DesiredState, d.Relocatable, d.RequiredTags, d.ConfigTags);
}
