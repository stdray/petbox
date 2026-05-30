using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Memory.Data;

// A note in a memory store, stored as a temporal (SCD type-2) row. Identity (Key)
// is the note name. Payload: Description + Body (markdown) + Tags (free CSV).
[Table("memory_entries")]
public sealed record MemoryEntry : TemporalRow
{
	[Column, NotNull] public string Description { get; init; } = string.Empty;
	[Column, NotNull] public string Body { get; init; } = string.Empty;
	[Column, NotNull] public string Tags { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is MemoryEntry m && m.Description == Description && m.Body == Body && m.Tags == Tags;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
