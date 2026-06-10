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
public readonly record struct SearchDoc(string Scope, string Type, string Id, string Text, string? Tags = null);

// A single match: the entity identity (type, id), a per-index relevance score (scales differ
// across indexes — the facade fuses by RANK, not raw score), and which retriever produced it.
public readonly record struct Hit(string Type, string Id, double Score, string? Retriever = null);

// Read-path narrowing. Minimal for the skeleton (entity type); grows as consumers need it.
public readonly record struct SearchFilter(string? Type = null);

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
	Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default);
}
