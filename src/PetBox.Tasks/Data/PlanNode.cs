using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

public enum PlanStatus { Pending, InProgress, Done, Blocked, Deferred, Cancelled }

// A node in a task board's plan tree, stored as a temporal (SCD type-2) row.
// Identity (Key) is the human path "Phase 16/Wave 1/WAL"; ordering is sparse
// Priority then Key. Payload: Status + Body + optional CommitRef + Priority.
[Table("plan_nodes")]
public sealed record PlanNode : TemporalRow
{
	[Column] public PlanStatus Status { get; init; }
	[Column, NotNull] public string Body { get; init; } = string.Empty;
	[Column, Nullable] public string? CommitRef { get; init; }
	[Column] public long Priority { get; init; }

	public override bool SamePayload(TemporalRow other) =>
		other is PlanNode p && p.Status == Status && p.Body == Body && p.CommitRef == CommitRef && p.Priority == Priority;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
