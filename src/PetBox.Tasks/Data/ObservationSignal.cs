using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// Row shape for `observation_signal` (M023_ObservationSignal) — see that migration's header
// for why this is its own table instead of a plan_nodes column.
[Table("observation_signal")]
public sealed record ObservationSignal
{
	[Column, PrimaryKey, NotNull] public string NodeId { get; init; } = string.Empty;
	[Column, NotNull] public long RecurrenceCount { get; init; } = 1;
	[Column, NotNull] public DateTime LastSeenAt { get; init; }
	[Column, Nullable] public DateTime? RecurredAfterFixAt { get; init; }
	[Column, Nullable] public string? FixedByNodeId { get; init; }
}
