using JetBrains.Annotations;
using LinqToDB.Mapping;

namespace PetBox.Deploy.Data;

// Actual run-state of a service's container on a node, as last reported by the agent. Stopped is
// a legal wire value an agent can report even though nothing in this repo constructs it today
// (Missing/Running are the two the sweeper/reconciler currently produce) — [UsedImplicitly] rather
// than removing a real state of the enum's own domain.
public enum ActualState
{
	Missing = 0,
	[UsedImplicitly] Stopped = 1,
	Running = 2,
}

// The last fact reported by a node-agent for one (NodeId, Service) pair — the actual
// side of the desired-vs-actual delta. Keyed by (NodeId, Service). Healthy reflects the
// container's HEALTHCHECK (true when running & healthy or no healthcheck defined).
// linq2db [Table]/[Column] entity: materialized by SELECT, not `new` + property-set the analyzer
// can trace (resharper-clt-step5g). ContainerId/ImageDigest are set on every agent report but not
// currently read back by any consumer.
[Table("deploy_deployment_status"), UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record DeploymentStatus
{
	[PrimaryKey(0), NotNull] public string NodeId { get; init; } = string.Empty;
	[PrimaryKey(1), NotNull] public string Service { get; init; } = string.Empty;
	[Column, NotNull] public ActualState ActualState { get; init; }
	[Column] public string? ContainerId { get; init; }
	[Column] public string? ImageDigest { get; init; }
	[Column, NotNull] public bool Healthy { get; init; }
	// Agent-reported reconcile error for this service (e.g. "caddy is not available on this
	// node" for a site, or a docker run failure). Null = no error.
	[Column] public string? Error { get; init; }
	[Column, NotNull] public DateTime ReportedAt { get; init; }
}
