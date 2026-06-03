using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// A node in a board's tree, stored as a temporal (SCD type-2) row. Identity (Key)
// is the path "a/b/c"; ordering is sparse Priority then Key. `Status` is a workflow
// SLUG (validated against the board's kind/type by WorkflowEngine, not by this
// record). `Type` is the task type (feature|bug on work boards; empty elsewhere).
[Table("plan_nodes")]
public sealed record PlanNode : TemporalRow
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
	[Column, Nullable] public string? CommitRef { get; init; }
	[Column] public long Priority { get; init; }

	public override bool SamePayload(TemporalRow other) =>
		other is PlanNode p && p.Status == Status && p.Type == Type && p.Name == Name && p.Body == Body && p.CommitRef == CommitRef && p.Priority == Priority;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
