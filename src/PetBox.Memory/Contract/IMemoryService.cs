using PetBox.Core.Contract;
using PetBox.Core.Models;
using PetBox.Memory.Data;

namespace PetBox.Memory.Contract;

// The single entry point to the Memory module for every caller (MCP tools, Razor
// pages). It owns store lifecycle plus the entry rules (taxonomy parsing, tag
// normalization, FTS search, temporal upsert + FTS rebuild), so adapters stay thin.
// A NetArchTest forbids Web tools/pages from reaching IMemoryStore / MemoryDb directly.
// It also implements the generic uniform-read contract (ISearchService — spec
// uniform-entity-verbs v2): list = search without a query, relevance = a sort option
// available only with a query. SearchEntriesAsync is the per-family method; the generic
// SearchAsync is its plain-envelope projection. The `scope` cascade (project ⊕ workspace)
// is an ADAPTER dimension — each call here reads ONE container (projectKey).
public interface IMemoryService : ISearchService<MemoryEntryHit, MemoryEntryFilter, MemorySortBy>
{
	// --- store lifecycle ---
	Task<MemoryStoreMeta> CreateStoreAsync(string projectKey, string store, string? description, CancellationToken ct = default);
	Task<IReadOnlyList<MemoryStoreMeta>> ListStoresAsync(string projectKey, CancellationToken ct = default);
	Task<bool> DeleteStoreAsync(string projectKey, string store, CancellationToken ct = default);
	Task<bool> StoreExistsAsync(string projectKey, string store, CancellationToken ct = default);

	// --- entries ---
	// Active entries ordered by key, optional taxonomy filter (User|Feedback|Project|Reference).
	Task<IReadOnlyList<MemoryEntryView>> ListAsync(string projectKey, string store, string? type, CancellationToken ct = default);
	Task<MemoryEntryView?> GetAsync(string projectKey, string store, string key, CancellationToken ct = default);
	// Hybrid search over active entries (lexical FTS5 ⊕ semantic vectors, RRF-fused),
	// ranked; optional taxonomy filter. `lexical`/`semantic` (null = enabled) toggle each
	// retriever; semantic is silently off when no embedding capability is available. The
	// result carries which retrievers ran and whether it degraded.
	Task<MemorySearchResult> SearchAsync(string projectKey, string store, string query, string? type, bool? lexical = null, bool? semantic = null, CancellationToken ct = default);
	// The unified read of ONE container (spec uniform-entity-verbs v2) behind memory_search.
	//   No Query  → deterministic LISTING over the stores in scope (Filter.Store or the
	//     implicit sweep, which skips sensitive stores); default order Updated desc (then
	//     key, then store), overridable by Sort (created/updated; Relevance is rejected
	//     without a query).
	//   With Query → relevance SELECTION (hybrid lexical FTS ⊕ semantic vectors, RRF-fused; the
	//     fused ranking supplies a bounded candidate pool of max(3×limit, 50) per store). Every
	//     store's pool is then fused GLOBALLY by RRF score (the best hit wins regardless of which
	//     store holds it), blended with freshness (time-decay) and MMR-diversified before the
	//     limit; an explicit created/updated Sort reorders WITHIN the selected set. Retrievers
	//     provenance is filled (OR across stores). MemoryEntryHit.Score carries the fused,
	//     decayed relevance so the adapter can honestly merge across scopes.
	// Filter.Type narrows in both modes. Limit caps the rows (0 = unbounded listing / the
	// pool bound with a query); BodyLen snippets row bodies (0 = full).
	Task<MemoryEntrySearchResult> SearchEntriesAsync(string projectKey, SearchRequest<MemoryEntryFilter, MemorySortBy> request, CancellationToken ct = default);
	// Declarative temporal upsert (+ soft-deletes), then FTS rebuild. PATCH semantics on
	// edits (version > 0): a null field keeps the active entry's current value, an explicit
	// empty ("") clears it; a new entry (version 0) maps null to empty. The result is a pure
	// write-ack (spec sinceversion-contract): Added/Updated/Removed cover ONLY this call's
	// entries — no cursor parameter on a write; CurrentVersion is the store-wide cursor to
	// feed DeltaAsync (the only delta/catch-up surface).
	Task<MemoryUpsertOutcome> UpsertAsync(string projectKey, string store, IReadOnlyList<MemoryEntryInput> upserts, IReadOnlyList<MemoryDelete> deletes, CancellationToken ct = default);
	Task<MemoryUpsertOutcome> DeltaAsync(string projectKey, string store, long sinceVersion, CancellationToken ct = default);

	// --- UI helper (store page renders the raw active entries) ---
	Task<IReadOnlyList<MemoryEntry>> ListActiveEntriesAsync(string projectKey, string store, CancellationToken ct = default);

	// --- usage telemetry, read side (the writer is IMemoryUsageRecorder) ---
	// Usage counters for the given keys (null = the whole store), keyed by entry key;
	// entries that never surfaced have no row and are absent from the map.
	Task<IReadOnlyDictionary<string, MemoryUsageView>> GetUsageAsync(string projectKey, string store, IReadOnlyCollection<string>? keys = null, CancellationToken ct = default);

	// Store-wide usage aggregate over the ACTIVE entry set (coverage, median recency of the
	// surfaced entries, and the never-surfaced dead tail). `deadTailLimit` caps the sampled
	// dead-tail keys (oldest-created first). Sibling to GetUsageAsync — same entry_usage source.
	Task<MemoryUsageAggregate> GetUsageAggregateAsync(string projectKey, string store, int deadTailLimit = 10, CancellationToken ct = default);
}
