using PetBox.Deploy.Data;

namespace PetBox.Deploy.Contract;

// --- nodes ---

// Operator input to register or update a node. Tags is the raw CSV capability vector.
public sealed record NodeInput(string Id, string DisplayName, string Tags, bool Ephemeral, string? KeyRef = null);

// Read view of a node. Online is computed by the service from LastSeenAt + a staleness
// window; Deployments is how many deployments target this node.
public sealed record NodeView(
	string Id,
	string DisplayName,
	string Tags,
	bool Ephemeral,
	string? KeyRef,
	DateTime? LastSeenAt,
	bool Online,
	int Deployments,
	DateTime CreatedAt);

// --- deployments ---

// Operator input for a deployment (desired state of a service on a node). Id null/empty
// = create (a new id is generated); set = update that deployment. ConfigHash is computed
// by the service, never supplied. RunSpec null = empty spec (image+env only).
public sealed record DeploymentInput(
	string? Id,
	string Service,
	string Project,
	string NodeId,
	string ImageDigest,
	DesiredState DesiredState,
	bool Relocatable,
	string RequiredTags,
	string ConfigTags,
	RunSpec? RunSpec = null);

// --- agent contract (pull) ---

// One desired deployment as the node-agent sees it on poll. The agent resolves env from
// /v1/conf using ConfigTags, runs ImageDigest to ConfigHash, and reconciles to Desired.
public sealed record PollItem(
	string Service,
	string Project,
	string ImageDigest,
	DesiredState Desired,
	string ConfigTags,
	string ConfigHash,
	RunSpec? RunSpec = null)
{
	// Resolved container env (config bundle for (Project, ConfigTags)). Populated by the
	// poll endpoint server-side so the node key needs no config:read. Null until resolved.
	public IReadOnlyDictionary<string, string>? Env { get; init; }
}

// The full desired state for a node. The agent stops any container whose Service is not
// in this list (self-fencing — it no longer owns that deployment).
public sealed record PollResponse(string NodeId, IReadOnlyList<PollItem> Deployments);

// One actual container state the agent reports for a service it manages.
public sealed record ActualReport(
	string Service,
	string? ContainerId,
	ActualState State,
	string? ImageDigest,
	bool Healthy);

// A node-agent's heartbeat: the actual state of every container it manages.
public sealed record HeartbeatReport(IReadOnlyList<ActualReport> Actual);

// --- failover ---

// The outcome of considering one relocatable deployment on a stale node. Relocated=true
// means NodeId was moved to ToNode; false means no online node matched (Reason explains).
public sealed record RescheduleAction(
	string DeploymentId,
	string Service,
	string FromNode,
	string? ToNode,
	bool Relocated,
	string Reason);

// Read view of a deployment with its last reported actual state (null = never reported).
public sealed record DeploymentView(
	string Id,
	string Service,
	string Project,
	string NodeId,
	string ImageDigest,
	DesiredState DesiredState,
	bool Relocatable,
	string RequiredTags,
	string ConfigTags,
	string ConfigHash,
	DateTime UpdatedAt,
	ActualState? ActualState,
	bool? Healthy,
	DateTime? ReportedAt,
	RunSpec RunSpec);
