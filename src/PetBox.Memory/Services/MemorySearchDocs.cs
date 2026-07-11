using PetBox.Core.Search;
using PetBox.Memory.Data;

namespace PetBox.Memory.Services;

// The single source of truth for how a memory entry maps onto the entity-addressed search
// contract (Scope, Type, Id, Text, Tags) — reused by the write seam, the lexical backfill, and
// the vectorization source so they can never drift.
//
// Post-merge every store of a project shares ONE file, hence ONE search_fts and ONE search_vec.
// The entity Type therefore carries the STORE — the same way tasks addresses a board (Type = board
// name): one lexical index + one vector index cover the whole project, and narrowing to a store —
// or to the sweep's subset of stores — is a `Type IN (…)` predicate (SearchFilter.Types), not N
// separate queries. The MemoryType taxonomy is NOT the index's Type: it stays a post-resolution
// filter (a removed entry's MemoryType is unknown at delete time).
public static class MemorySearchDocs
{
	public static SearchDoc ToDoc(MemoryEntry e, string scope) =>
		new(scope, e.Store, e.Key, e.Description + "\n" + e.Body, e.Tags);
}
