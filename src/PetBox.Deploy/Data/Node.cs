using LinqToDB.Mapping;

namespace PetBox.Deploy.Data;

// A machine in the fleet that runs a node-agent. Identity (Id) is an operator-chosen
// slug (e.g. "vdsina-1", "local-wsl"). Tags are the capability vector (free CSV of
// "ns:value" / flags, e.g. "net.x,disk=nvme") used to match deployment RequiredTags.
// KeyRef points at the node-scoped ApiKey the agent authenticates with. LastSeenAt is
// the last heartbeat; null = never reported. Mutable row (not temporal).
[Table("deploy_node")]
public sealed record Node
{
	[PrimaryKey, NotNull] public string Id { get; init; } = string.Empty;
	[Column, NotNull] public string DisplayName { get; init; } = string.Empty;
	[Column, NotNull] public string Tags { get; init; } = string.Empty;
	// Agent-detected host capabilities (CSV, e.g. "docker,caddy"), refreshed by heartbeat —
	// distinct from operator-set Tags. Empty until a capability-aware agent reports.
	[Column, NotNull] public string Capabilities { get; init; } = string.Empty;
	// The agent's last host snapshot (HostReport JSON: security/memory/disk/os), refreshed
	// by heartbeat. "{}" until a host-report-aware agent reports.
	[Column, NotNull] public string HostReport { get; init; } = "{}";
	[Column, NotNull] public bool Ephemeral { get; init; }
	[Column] public string? KeyRef { get; init; }
	[Column] public DateTime? LastSeenAt { get; init; }
	[Column, NotNull] public DateTime CreatedAt { get; init; }
}
