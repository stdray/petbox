using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Memory.Data;

// Memory entry taxonomy — mirrors the coding-agent memory schema (user | feedback
// | project | reference). A closed, mutually-exclusive classification (drives
// recall filtering and write-time guidance), distinct from the open free-form Tags.
// Order is load-bearing: Project = 2 is the legacy backfill default in M002.
public enum MemoryType
{
	User = 0,
	Feedback = 1,
	Project = 2,
	Reference = 3,
}

// A note in a memory store, stored as a temporal (SCD type-2) row. Identity (Key)
// is the note name. Payload: Type + Description + Body (markdown) + Tags (free CSV).
[Table("memory_entries")]
public sealed record MemoryEntry : TemporalRow
{
	[Column, NotNull] public MemoryType Type { get; init; } = MemoryType.Project;
	[Column, NotNull] public string Description { get; init; } = string.Empty;
	[Column, NotNull] public string Body { get; init; } = string.Empty;
	[Column, NotNull] public string Tags { get; init; } = string.Empty;
	// Free-form structured metadata (JSON string) for round-tripping arbitrary
	// client key/values; opaque to the service and NOT FTS-indexed.
	[Column, NotNull] public string Metadata { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is MemoryEntry m && m.Type == Type && m.Description == Description && m.Body == Body && m.Tags == Tags && m.Metadata == Metadata;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
