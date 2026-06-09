using LinqToDB.Mapping;

namespace PetBox.Memory.Data;

// Vector mirror of the active memory entries for semantic search: one embedding per
// active entry, keyed by entry Key. Holds the embedding model + dimension alongside the
// packed float32 BLOB so the query path can model/dim-guard candidates (only fuse rows
// embedded by the same model at the same dim as the query). Written on upsert when an
// LLM embed capability is available; absent rows simply mean lexical-only for that entry.
[Table("memory_vec")]
public sealed class MemoryVec
{
	[Column, PrimaryKey] public string Key { get; set; } = string.Empty;
	[Column] public string Model { get; set; } = string.Empty;
	[Column] public int Dim { get; set; }
	[Column] public byte[] Vec { get; set; } = [];
}
