using LinqToDB.Data;
using Microsoft.Extensions.Logging;

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
public sealed partial class SearchService
{
	readonly IReadOnlyList<ISearchIndex> _indexes;
	// Optional: the facade is constructed per query by consumers that have no DI graph at hand
	// (MemoryService, TasksService, …). Null → the degradation is still REPORTED in the response
	// provenance, just not logged.
	readonly ILogger? _log;

	public SearchService(IEnumerable<ISearchIndex> indexes, ILogger? log = null)
	{
		_indexes = indexes.ToList();
		_log = log;
	}

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

	// `selection` is the SELECTION axis (spec: search-leg-classification / search-selection-vs-presentation):
	//   Relevance  — the fused top-K ask: poll EVERY leg, RRF-fuse, truncate to k. Vector-only
	//     candidates enter the output as peers.
	//   Enumerable — the scan/field ask: poll only ENUMERABLE legs (the TopK/vector leg is
	//     categorically excluded because it has no "all that matched"), return the FULL fused set
	//     WITHOUT truncation, and report `semantic:false` — a visible contract limit, not a silent
	//     drop. The consumer decides presentation order + its own limit.
	public async Task<SearchResponse> SearchAsync(string scope, string query, SearchFilter filter, int k,
		SearchSelection selection = SearchSelection.Relevance, CancellationToken ct = default)
	{
		var rankings = new List<IReadOnlyList<string>>();
		var byId = new Dictionary<string, Hit>();
		bool lexical = false, semantic = false, degraded = false;
		string? reason = null;

		foreach (var ix in _indexes)
		{
			// Participation is DERIVED from the leg classification: an enumerable selection needs the
			// full matched set, which a TopK leg cannot supply, so it never runs there.
			if (selection == SearchSelection.Enumerable && ix.LegClass != SearchLegClass.Enumerable)
				continue;
			try
			{
				var hits = await ix.SearchAsync(scope, query, filter, k, ct);
				rankings.Add(hits.Select(h => Key(h.Type, h.Id)).ToList());
				foreach (var h in hits)
					byId.TryAdd(Key(h.Type, h.Id), h); // first index to surface an entity owns the displayed hit
				if (ix.Capability.HasFlag(SearchCapability.Lexical)) lexical = true;
				if (ix.Capability.HasFlag(SearchCapability.Vector)) semantic = true;
			}
			catch (Exception ex)
			{
				// An index that should have answered failed → degrade honestly (the requested
				// capability flag stays false; the response says so) rather than silently drop it.
				// The failure is CLASSIFIED (an embedder adapter hands us a code; anything else is
				// index-error) so the caller learns WHY, and LOGGED so a silent hole — e.g. a
				// project with no Embed route, where semantic search never ran at all — shows up on
				// day one instead of never. First failure owns the reason.
				degraded = true;
				var code = ex is SearchDegradedException sde ? sde.Reason : SearchDegradedReason.IndexError;
				reason ??= code;
				if (_log is not null)
					LogIndexDegraded(_log, ix.GetType().Name, ix.Capability.ToString(), scope, code, ex);
			}
		}

		// Fuse by rank and carry the FUSED RRF score on each hit (overwriting the per-index Score,
		// which is meaningless post-fusion) — a rank-based score comparable across separate
		// SearchService calls, so a consumer can globally merge several per-container pools by it.
		// Relevance selection truncates to k (the fused top-K); an enumerable selection returns the
		// WHOLE matched set (no truncation — the leg is enumerable precisely so scan gets all of it)
		// and reports semantic:false, since the vector leg categorically did not participate.
		var fused = HybridMerge.RrfScored([.. rankings]);
		var ranked = selection == SearchSelection.Enumerable ? fused : fused.Take(k);
		var hitsOut = ranked.Select(f => byId[f.Key] with { Score = f.Score }).ToList();
		if (selection == SearchSelection.Enumerable) semantic = false;
		return new SearchResponse(hitsOut, new SearchRetrievers(lexical, semantic, degraded, reason));
	}

	// Fusion identity = the entity address (type, id) within the queried scope. The unit
	// separator keeps composite keys collision-free.
	static string Key(string type, string id) => type + "\x1f" + id;

	[LoggerMessage(EventId = 400, Level = LogLevel.Warning,
		Message = "search: index {Index} ({Capability}) failed in scope '{Scope}' → degraded, reason {Reason}")]
	static partial void LogIndexDegraded(ILogger logger, string index, string capability, string scope, string reason, Exception ex);
}
