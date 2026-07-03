using LinqToDB.Data;

namespace PetBox.Core.Search;

// The one door into search, in front of N pluggable indexes. It routes the write path by
// consistency class and fuses the read path with provenance — so consumers (memory, tasks,
// session) speak one entity-addressed contract regardless of which/where the indexes are
// (spec: search-service umbrella). Design: memory m-b3fbe908 (classes) + m-1a5c37fe (fusion).
//
//   Write — Synchronous indexes run INLINE on the caller's entity transaction (the lexical
//     floor commits/rolls back with the entity). Eventual indexes are NOT driven here: they
//     subscribe to the temporal log via their own cursor (async-vectorization) — a separate
//     work item; the facade simply skips them on write.
//   Read  — poll every index, RRF-fuse their ranked id lists (reuse HybridMerge), and report
//     which retrievers ran + whether the result is degraded.
public sealed class SearchService
{
	readonly IReadOnlyList<ISearchIndex> _indexes;

	public SearchService(IEnumerable<ISearchIndex> indexes) => _indexes = indexes.ToList();

	public async Task IndexAsync(DataConnection tx, SearchDoc doc, CancellationToken ct = default)
	{
		foreach (var ix in _indexes)
			if (ix.ConsistencyClass == SearchConsistency.Synchronous)
				await ix.IndexAsync(tx, doc, ct);
	}

	public async Task DeleteAsync(DataConnection tx, string scope, string type, string id, CancellationToken ct = default)
	{
		foreach (var ix in _indexes)
			if (ix.ConsistencyClass == SearchConsistency.Synchronous)
				await ix.DeleteAsync(tx, scope, type, id, ct);
	}

	public async Task<SearchResponse> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
	{
		var rankings = new List<IReadOnlyList<string>>();
		var byId = new Dictionary<string, Hit>();
		bool lexical = false, semantic = false, degraded = false;

		foreach (var ix in _indexes)
		{
			try
			{
				var hits = await ix.SearchAsync(scope, query, filter, k, ct);
				rankings.Add(hits.Select(h => Key(h.Type, h.Id)).ToList());
				foreach (var h in hits)
					byId.TryAdd(Key(h.Type, h.Id), h); // first index to surface an entity owns the displayed hit
				if (ix.Capability.HasFlag(SearchCapability.Lexical)) lexical = true;
				if (ix.Capability.HasFlag(SearchCapability.Vector)) semantic = true;
			}
			catch
			{
				// An index that should have answered failed → degrade honestly (the requested
				// capability flag stays false; the response says so) rather than silently drop it.
				degraded = true;
			}
		}

		// Fuse by rank and carry the FUSED RRF score on each hit (overwriting the per-index Score,
		// which is meaningless post-fusion) — a rank-based score comparable across separate
		// SearchService calls, so a consumer can globally merge several per-container pools by it.
		var fused = HybridMerge.RrfScored([.. rankings]);
		var hitsOut = fused.Take(k).Select(f => byId[f.Key] with { Score = f.Score }).ToList();
		return new SearchResponse(hitsOut, new SearchRetrievers(lexical, semantic, degraded));
	}

	// Fusion identity = the entity address (type, id) within the queried scope. The unit
	// separator keeps composite keys collision-free.
	static string Key(string type, string id) => type + "\x1f" + id;
}
