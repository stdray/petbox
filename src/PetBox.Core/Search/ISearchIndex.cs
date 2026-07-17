using LinqToDB.Data;

namespace PetBox.Core.Search;

// The consistency class of an index's write path — the load-bearing distinction of the
// search contract (design: memory m-b3fbe908).
//   Synchronous = the lexical floor: updated INSIDE the entity's DB transaction (same
//     SQLite file/connection), commits/rolls back WITH the entity → a committed entity is
//     never lexically-stale. MUST be local (transactional ⊥ remote; no 2PC).
//   Eventual    = vector / episodic enrichment: materialized later by a background worker
//     that subscribes to the temporal log via its own cursor (async-vectorization). May be
//     remote/pluggable; best-effort; search degrades gracefully when it is behind or absent.
public enum SearchConsistency
{
	Synchronous,
	Eventual,
}

// What an index can answer. Flags so one index could declare several; the facade lifts
// these to read-path provenance (which retrievers actually ran).
[Flags]
public enum SearchCapability
{
	None = 0,
	Lexical = 1,
	Vector = 2,
}

// A document to index, addressed by ENTITY (scope, type, id) — never by row. Resolving the
// entity back from (type, id) is the consumer's job; the contract only carries the searchable
// text + optional free tags. (spec: search-entity-addressed.)
//
// `Key` (search-key-column-everywhere) is the entity's own business key/slug — n.Key for a task
// node, e.Key for a memory entry — projected into its OWN indexed column instead of being spliced
// into `Text`. Slugs are English kebab while titles/bodies are often Russian; a dedicated column
// keeps the key's tokens searchable WITHOUT mixing them into the prose term frequencies (a splice
// there double-counts the key's words and skews BM25 — see TasksSearchDocs' history). Distinct
// from `Id`: `Id` is the (unindexed) row ADDRESS an index resolves a hit back to, which for a
// comment is a namespaced "c:"+guid, not a word a caller would type. `Key` is OPTIONAL (default
// "") — an entity with no meaningful lexicon key (e.g. a comment, addressed by a random GUID)
// simply leaves it empty; an index with no dedicated Key column (none yet outside SqliteFtsIndex)
// ignores it.
public readonly record struct SearchDoc(string Scope, string Type, string Id, string Text, string? Tags = null, string Key = "");

// A single match: the entity identity (type, id), a per-index relevance score (scales differ
// across indexes — the facade fuses by RANK, not raw score), and which retriever produced it.
public readonly record struct Hit(string Type, string Id, double Score, string? Retriever = null);

// A FACET predicate pushed INTO each leg's candidate query, applied BEFORE the leg truncates at
// k (spec search-facet-pushdown) — joined against the search_meta reference layer by the entity
// address (Scope, Type, Id), the SAME address the text/vector rows carry. The pushdown is what
// lets the caller drop a compensating over-fetch pool: a candidate a facet excludes never occupies
// a top-k slot, so it never has to be re-fetched around.
//
// `ExcludeStatusKinds` drops entities whose search_meta.StatusKind is in the set — the general
// mechanism; the tasks-specific VALUE (hide terminal-cancel unless includeClosed) is chosen by the
// caller. An entity with NO search_meta row (e.g. a tasks comment doc, which carries text but no
// facet row) is KEPT — a facet it does not carry cannot hide it, matching the pre-pushdown behavior
// where such a hit resolved through its owner. Null/empty set = neutral (no facet narrowing).
public readonly record struct FacetFilter(IReadOnlyList<string>? ExcludeStatusKinds = null);

// Read-path narrowing. `Type` pins ONE entity type; `Types` is an include-SET over the same
// column — the seam that lets a consumer whose containers share one file (memory stores in
// memory/{project}.db, Type = store name) narrow a SINGLE index query to several containers at
// once, instead of running one query per container and merging by hand. Null/empty = no
// narrowing on that axis; when both are set, both must hold. `Facets` is the OPTIONAL facet
// pushdown (search-facet-pushdown): null — the default, and what a file with no search_meta table
// (memory today) always passes — emits no join, so it is a no-op there.
public readonly record struct SearchFilter(string? Type = null, IReadOnlyList<string>? Types = null, FacetFilter? Facets = null);

// A fused search response: the ranked hits plus honest provenance (which retrievers ran and
// whether the result is degraded). (spec: search-provenance.)
public readonly record struct SearchResponse(IReadOnlyList<Hit> Hits, SearchRetrievers Retrievers);

// One pluggable index behind the search contract. The facade (SearchService) routes writes by
// ConsistencyClass and fuses reads across every registered index.
//
// Writes take the caller's open transaction (`tx`): a Synchronous index MUST use it so its
// update joins the entity's commit/rollback. An Eventual index ignores `tx` (it is driven by a
// background cursor, not the write path) — the facade passes null for it.
public interface ISearchIndex
{
	SearchConsistency ConsistencyClass { get; }
	SearchCapability Capability { get; }

	Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default);
	Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default);
	// Board/type-wide purge: remove EVERY doc under (scope, type) in one shot. Used when a whole
	// container (e.g. a task board) is dropped and its per-id docs would otherwise be orphaned.
	Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default);
	Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default);
}
