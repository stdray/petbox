using PetBox.Core.Search;
using PetBox.Memory.Data;

namespace PetBox.Memory.Services;

// The single source of truth for how a memory entry maps onto the entity-addressed search
// contract (Scope, Type, Id, Text, Tags) — reused by the write seam, the lexical backfill, and
// the vectorization source so they can never drift. Memory has no partition, so a removed
// entry's MemoryType is unknown at delete time; therefore Type is a CONSTANT and the MemoryType
// filter is applied after resolving hits to entries (as it always was), not pushed into the index.
public static class MemorySearchDocs
{
	// Entity type in the index — constant for all memory entries (see note above).
	public const string Type = "memory";

	// Cursor / index name for the Class-B vector index living in each memory store file.
	public const string VectorIndex = "vector";

	public static SearchDoc ToDoc(MemoryEntry e, string scope) =>
		new(scope, Type, e.Key, e.Description + "\n" + e.Body, e.Tags);
}
