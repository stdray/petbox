using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// A node in a board's tree, stored as a temporal (SCD type-2) row. Identity (Key)
// is the path "a/b/c"; ordering is sparse Priority then Key. `Status` is a workflow
// SLUG (validated against the board's kind/type by WorkflowEngine, not by this
// record). `Type` is the task type (feature|bug on work boards; empty elsewhere).
[Table("plan_nodes")]
public sealed record TaskNode : TemporalRow
{
	// Partition: which board this node belongs to. All boards of a project share one
	// plan_nodes table (one file per project), scoped by Board — so Key uniqueness and
	// the version cursor are per-board. Set by the service; carried across revisions by
	// AsRevision. NOT part of SamePayload (it's partition identity, never an edit).
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	// Stable identity: assigned at birth, carried across revisions AND renames
	// (the upsert layer copies it from the prior/source row). Relations bind to
	// this, not to Key, so links survive a re-key. NOT part of SamePayload.
	[Column, NotNull] public string NodeId { get; init; } = string.Empty;
	[Column, NotNull] public string Status { get; init; } = string.Empty;
	[Column, NotNull] public string Type { get; init; } = string.Empty;
	// Short human title shown as the node heading; Body holds the longer detail.
	[Column, NotNull] public string Name { get; init; } = string.Empty;
	[Column, NotNull] public string Body { get; init; } = string.Empty;
	[Column] public long Priority { get; init; }

	// owner-decision-pending-flag: "this node is waiting on a decision from the owner",
	// ORTHOGONAL to Status — a node can be InProgress AND waiting. It is a real PAYLOAD field
	// (see SamePayload below), so flipping it mints a node revision. That is the point, not a
	// side effect: the flip is exactly the event the owner digest reads off tasks_delta, and a
	// field the payload comparer could not see would be silently dropped as a no-op by the
	// temporal layer.
	[Column, NotNull] public bool DecisionPending { get; init; }

	// node-origin-provenance, WRITE-ONCE half: the session this node was CREATED in. Set only
	// on the create (TasksService.Merge), inherited verbatim by every later revision, and NEVER
	// filled in afterwards — a node was not born in whichever session happened to edit it next,
	// so an empty value is a permanent, honest property of that node, not a hole to backfill.
	// Deliberately NOT in SamePayload/ChangedPayloadFields: it cannot change on an existing
	// node, so listing it there would only add a field that can never appear in a conflict.
	[Column, NotNull] public string OriginSessionId { get; init; } = string.Empty;

	// Attached commits (node-commits-impl): NOT a stored column and NOT part of the payload
	// (like tags, commits are an SCD-2 association in plan_node_commits, so editing them never
	// mints a node revision). Read paths populate this for enrichment; empty by default.
	[NotColumn] public IReadOnlyList<string> Commits { get; init; } = [];

	// node-origin-provenance, ACCUMULATING half (plan_node_sessions): the UNION of sessions that
	// have touched this node. Like Commits: NOT a stored column and NOT part of the payload, so
	// growing it never mints a node revision — the whole reason recurrence provenance is an
	// association and not a field (a bucket of N sessions as a field would be N revisions).
	// Read paths populate this for enrichment; empty by default.
	[NotColumn] public IReadOnlyList<string> OriginSessions { get; init; } = [];

	public override bool SamePayload(TemporalRow other) =>
		other is TaskNode p && p.Status == Status && p.Type == Type && p.Name == Name && p.Body == Body && p.Priority == Priority
		&& p.DecisionPending == DecisionPending;

	// Wire-facing names (title, not Name) — these land in a Stale conflict's
	// ChangedFields. Mirrors SamePayload field-for-field.
	public override IReadOnlyList<string> ChangedPayloadFields(TemporalRow other)
	{
		if (other is not TaskNode p) return [];
		var fields = new List<string>();
		if (p.Status != Status) fields.Add("status");
		if (p.Type != Type) fields.Add("type");
		if (p.Name != Name) fields.Add("title");
		if (p.Body != Body) fields.Add("body");
		if (p.Priority != Priority) fields.Add("priority");
		if (p.DecisionPending != DecisionPending) fields.Add("decisionPending");
		return fields;
	}

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
