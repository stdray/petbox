using LinqToDB.Mapping;

namespace PetBox.Memory.Data;

// FTS5 mirror of the active memory entries, rebuilt on every upsert. Not temporal
// — it only ever holds the current active set, keyed by entry Key (UNINDEXED in the
// virtual table; Description/Body/Tags are the indexed columns matched against).
[Table("memory_fts")]
public sealed class MemoryFts
{
	[Column] public string Key { get; set; } = string.Empty;
	[Column] public string Description { get; set; } = string.Empty;
	[Column] public string Body { get; set; } = string.Empty;
	[Column] public string Tags { get; set; } = string.Empty;
}
