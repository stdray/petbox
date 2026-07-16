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
	// The lexical projection's SCHEMA version (reindex-as-first-class-mechanism). MemoryService's
	// EnsureLexicalBackfillAsync gates its per-store rebuild on this number (marker key
	// MemoryCursors.Lexical(store)) instead of "this store already has a search_fts row" — that
	// guard could never re-fire once a store had ANY row, so a ToDoc shape change would need an
	// empty-the-table migration to reach already-populated files. That path is a trap here
	// specifically: emptying search_fts ahead of LegacyStoreMerge racing to copy rows across from a
	// legacy per-store file breaks the merge (2 red MemoryStoreMergeTests when tried). The version
	// gate reprojects lazily on the next search instead, so no migration ever needs to touch
	// search_fts again. Bump this whenever ToDoc's projected TEXT shape changes.
	public const long LexicalProjectionVersion = 1;

	public static SearchDoc ToDoc(MemoryEntry e, string scope) =>
		new(scope, e.Store, e.Key, e.Description + "\n" + e.Body, e.Tags);
}
