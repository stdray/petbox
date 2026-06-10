using LinqToDB.Mapping;

namespace PetBox.Deploy.Data;

// Desired run-state of a service on a node — the operator's intent. Stored as int.
public enum DesiredState
{
	Stopped = 0,
	Running = 1,
}

// The desired state of one service on one node: the single source of truth the agent
// reconciles toward. Id is a generated slug/guid. Service is the logical service name
// (one service may have N deployments across nodes — copies). ImageDigest pins the
// image to run. ConfigTags is the tag-vector the agent resolves env from via /v1/conf.
// RequiredTags must be ⊆ a node's Tags for that node to be a legal placement / failover
// target. Relocatable gates auto-failover. ConfigHash = hash(image+config+desired) so
// the agent knows when to recreate the container. Mutable row (not temporal).
[Table("deploy_deployment")]
public sealed record Deployment
{
	[PrimaryKey, NotNull] public string Id { get; init; } = string.Empty;
	[Column, NotNull] public string Service { get; init; } = string.Empty;
	// The project the service belongs to — whose config (workspace) applies. Env is
	// resolved server-side at poll from (Project, ConfigTags); the node key stays minimal.
	[Column, NotNull] public string Project { get; init; } = string.Empty;
	[Column, NotNull] public string NodeId { get; init; } = string.Empty;
	[Column, NotNull] public string ImageDigest { get; init; } = string.Empty;
	[Column, NotNull] public DesiredState DesiredState { get; init; } = DesiredState.Running;
	[Column, NotNull] public bool Relocatable { get; init; }
	[Column, NotNull] public string RequiredTags { get; init; } = string.Empty;
	[Column, NotNull] public string ConfigTags { get; init; } = string.Empty;
	[Column, NotNull] public string ConfigHash { get; init; } = string.Empty;
	[Column, NotNull] public DateTime UpdatedAt { get; init; }
}
