using PetBox.Core.Models;
using PetBox.Memory.Data;

namespace PetBox.Memory.Contract;

// The single entry point to the Memory module for every caller (MCP tools, Razor
// pages). It owns store lifecycle plus the entry rules (taxonomy parsing, tag
// normalization, FTS search, temporal upsert + FTS rebuild), so adapters stay thin.
// A NetArchTest forbids Web tools/pages from reaching IMemoryStore / MemoryDb directly.
public interface IMemoryService
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
	// Declarative temporal upsert (+ soft-deletes), then FTS rebuild.
	Task<MemoryUpsertOutcome> UpsertAsync(string projectKey, string store, IReadOnlyList<MemoryEntryInput> upserts, IReadOnlyList<MemoryDelete> deletes, long sinceVersion = 0, CancellationToken ct = default);
	Task<MemoryUpsertOutcome> DeltaAsync(string projectKey, string store, long sinceVersion, CancellationToken ct = default);

	// --- UI helper (store page renders the raw active entries) ---
	Task<IReadOnlyList<MemoryEntry>> ListActiveEntriesAsync(string projectKey, string store, CancellationToken ct = default);

	// --- usage telemetry, read side (the writer is IMemoryUsageRecorder) ---
	// Usage counters for the given keys (null = the whole store), keyed by entry key;
	// entries that never surfaced have no row and are absent from the map.
	Task<IReadOnlyDictionary<string, MemoryUsageView>> GetUsageAsync(string projectKey, string store, IReadOnlyCollection<string>? keys = null, CancellationToken ct = default);
}
