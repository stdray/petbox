using LinqToDB.Mapping;

namespace PetBox.Sessions.Data;

// A session transcript stored as a single flat row — the latest snapshot, no history.
// ContentZ is a Brotli-compressed JSONL message blob (see SessionContent). Version == the
// last message's ordinal: a monotonic content cursor, NOT optimistic concurrency. A session
// has one writer (the agent, re-pushing the full transcript), so the temporal SCD-2 machinery
// that used to back this row was pure overhead + a bloat multiplier (one full snapshot per turn).
[Table("sessions")]
public sealed record SessionRow
{
	[Column, PrimaryKey, NotNull] public string SessionId { get; init; } = string.Empty;
	[Column, NotNull] public string Agent { get; init; } = string.Empty;
	[Column, NotNull] public byte[] ContentZ { get; init; } = Array.Empty<byte>();
	[Column] public long Version { get; init; }
	[Column, NotNull] public DateTime Updated { get; init; }
}
