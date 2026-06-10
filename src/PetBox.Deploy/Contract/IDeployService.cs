namespace PetBox.Deploy.Contract;

// The single entry point to the Deploy module for every caller (agent endpoints, MCP
// tools, Razor pages). Owns the fleet-wide deploy db: the node registry and the
// per-(service,node) desired state, plus computed views (online, actual-state join).
// A NetArchTest forbids Web from reaching DeployDb directly.
public interface IDeployService
{
	// --- nodes ---
	Task<NodeView> UpsertNodeAsync(NodeInput input, CancellationToken ct = default);
	Task<IReadOnlyList<NodeView>> ListNodesAsync(CancellationToken ct = default);
	Task<NodeView?> GetNodeAsync(string id, CancellationToken ct = default);
	Task<bool> DeleteNodeAsync(string id, CancellationToken ct = default);

	// --- deployments ---
	// Create (Id null/empty) or update (Id set) a deployment. Enforces one deployment per
	// (Service, NodeId). Computes ConfigHash. Returns the resulting view.
	Task<DeploymentView> UpsertDeploymentAsync(DeploymentInput input, CancellationToken ct = default);
	// Active deployments, optionally filtered by node and/or service, joined with last status.
	Task<IReadOnlyList<DeploymentView>> ListDeploymentsAsync(string? nodeId = null, string? service = null, CancellationToken ct = default);
	Task<DeploymentView?> GetDeploymentAsync(string id, CancellationToken ct = default);
	Task<bool> DeleteDeploymentAsync(string id, CancellationToken ct = default);

	// --- agent contract (pull) ---
	// The desired deployments assigned to a node. Bumps the node's LastSeenAt (poll is the
	// agent's regular outbound contact = the liveness signal failover watches).
	Task<PollResponse> PollAsync(string nodeId, CancellationToken ct = default);
	// Record the node's reported actual container states and bump LastSeenAt.
	Task ApplyHeartbeatAsync(string nodeId, HeartbeatReport report, CancellationToken ct = default);

	// --- failover ---
	// Relocate relocatable deployments off nodes whose last contact is older than `staleness`
	// onto an online node whose tags cover RequiredTags (one-copy-per-node preserved). Failure
	// mode is double-run (the stale node's agent self-fences on return), never data loss.
	Task<IReadOnlyList<RescheduleAction>> RescheduleStaleAsync(TimeSpan staleness, CancellationToken ct = default);
}
