using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// A typed directed edge between two nodes, stored in the PROJECT's tasks file
// (tasks/{project}.db) right next to plan_nodes — so the endpoints can carry a REAL
// foreign key (relations-in-project-db). There is no ProjectKey column: the FILE is the
// project identity. Edges were always strictly intra-project (one project key per row,
// both endpoints resolved under it), so nothing is lost by dropping the column.
//
// Endpoints reference plan_node_ids(NodeId) — the node-identity registry that triggers
// keep in lockstep with plan_nodes (see M014_Relations). plan_nodes itself is temporal
// (SCD-2: many revisions per NodeId), so it cannot BE a FK parent; the registry is the
// one row per node identity that can. FK is ON DELETE CASCADE: when a board delete
// hard-removes a node's last revision, its edges go with it instead of dangling.
//
// Interval-temporal: an edge is active while ClosedAt is null; delete/FSM effects
// soft-close it (history kept). Binds to the stable NodeId, so edges survive renames.
[Table("relations")]
public sealed record Relation
{
	[Column, PrimaryKey, NotNull] public string Id { get; init; } = string.Empty;
	[Column, NotNull] public string Kind { get; init; } = string.Empty;
	[Column, NotNull] public string FromNodeId { get; init; } = string.Empty;
	[Column, NotNull] public string ToNodeId { get; init; } = string.Empty;
	[Column, NotNull] public DateTime CreatedAt { get; init; }
	// Interval-temporal: null = active edge; set = the edge was retired at this time.
	// Soft-close keeps history ("B was blocked by A from CreatedAt to ClosedAt").
	[Column, Nullable] public DateTime? ClosedAt { get; init; }
}

// The node-identity registry (plan_node_ids): exactly one row per stable NodeId that has
// at least one revision in plan_nodes. NOT written by application code — SQLite triggers
// derive it from plan_nodes (insert → register, last revision deleted → unregister), so
// it cannot drift from the nodes it indexes. Exists solely to be a legal FK parent for
// relations.From/ToNodeId, which the temporal plan_nodes table cannot be.
[Table("plan_node_ids")]
public sealed record PlanNodeId
{
	[Column, PrimaryKey, NotNull] public string NodeId { get; init; } = string.Empty;
}
