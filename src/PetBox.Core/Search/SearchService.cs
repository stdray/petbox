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
	// The штатный PRECISION mode (spec: search-rerank-in-loop): when a reranker is supplied, a
	// RELEVANCE selection reranks the deduped candidate union with a cross-encoder and reports
	// Reranked=true. Null → the facade stays pure RRF (DegradedRrf), Reranked=false — an honest
	// degradation, and the seam a test uses to force RRF ordering (pass no reranker).
	readonly IReranker? _reranker;
	// The candidate pool the reranker is allowed to carry — a DECLARED number (default 160, spec:
	// search-rerank-candidate-budget), not derived from latency — the cap that keeps the enumerable
	// lexical leg's full matched set from flooding the cross-encoder. Only the top `budget` of the
	// fused pool reaches the reranker.
	readonly RerankCandidateBudget _budget;

	public SearchService(IEnumerable<ISearchIndex> indexes, ILogger? log = null, IReranker? reranker = null, RerankCandidateBudget? budget = null)
	{
		_indexes = indexes.ToList();
		_log = log;
		_reranker = reranker;
		_budget = budget ?? new RerankCandidateBudget();
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
	// `mode` is the RANKING axis the caller chose (spec: search-ranking-mode-is-caller-choice) — the
	// facade never infers it from _reranker's presence. Precision (the default here) preserves this
	// facade's long-standing behavior for call sites that predate the mode (still the honest choice
	// when nothing says otherwise); Speed short-circuits straight to RRF below WITHOUT ever awaiting
	// IsRerankAvailableAsync, whether or not a reranker happens to be wired. The EDGE default per
	// caller type (MCP verb vs UI page) is decided upstream — never guessed here.
	public async Task<SearchResponse> SearchAsync(string scope, string query, SearchFilter filter, int k,
		SearchSelection selection = SearchSelection.Relevance,
		SearchRankingMode mode = SearchRankingMode.Precision,
		CandidateTextResolver? resolveCandidateText = null, CancellationToken ct = default)
	{
		var legs = await PollAsync(scope, query, filter, k, selection, ct);
		var fused = HybridMerge.RrfScored([.. legs.Rankings]);

		// An ENUMERABLE selection is not a ranking ask at all: it returns the WHOLE matched set with no
		// truncation and no pool, reports semantic:false (the vector leg categorically did not run) and
		// leaves Ranking null. Unchanged by the pageable work — a scan has no relevance pool to page.
		if (selection == SearchSelection.Enumerable)
			return new SearchResponse(
				fused.Select(f => legs.ById[f.Key] with { Score = f.Score }).ToList(),
				new SearchRetrievers(legs.Lexical, Semantic: false, legs.Degraded, legs.Reason, Ranking: null));

		// A RELEVANCE selection is now expressed as "rank the pool, then take the first k". The top-k a
		// non-paging caller sees is byte-for-byte what it was before (the pool is ordered by exactly the
		// same rules, and the pool cap only bites past ~160 — still deeper than any k this codebase asks
		// for, but the margin is now thin: PageSizeOptions.Allowed tops out at 100, so a caller asking
		// for the largest page sits inside the same order of magnitude as the cap rather than far below it);
		// the difference is that the REST of the order now survives the call instead of being discarded.
		var pool = await RankPoolAsync(scope, query, mode, resolveCandidateText, legs, fused, ct);
		return new SearchResponse([.. pool.Ordered.Take(k)], pool.Retrievers);
	}

	// The PAGEABLE entry point (spec: result-set-pageable): the FULL ranked pool rather than its first
	// k rows. Callers that page hold this — once — and serve pages out of it; SearchAsync above is the
	// same computation with a `Take(k)` stapled on, so the two can never disagree about order.
	//
	// `legK` is still the per-leg top-K (it is what bounds the VECTOR leg, which has no "all that
	// matched"); it deliberately does NOT bound the pool. The lexical leg is enumerable — it returns
	// everything the facet predicate left — so the pool's real ceiling is the rerank candidate budget,
	// which is what SearchPool.PoolLimit reports and what PoolBounded says was hit.
	public async Task<SearchPool> SearchPoolAsync(string scope, string query, SearchFilter filter, int legK,
		SearchRankingMode mode = SearchRankingMode.Precision,
		CandidateTextResolver? resolveCandidateText = null, CancellationToken ct = default)
	{
		var legs = await PollAsync(scope, query, filter, legK, SearchSelection.Relevance, ct);
		return await RankPoolAsync(scope, query, mode, resolveCandidateText, legs,
			HybridMerge.RrfScored([.. legs.Rankings]), ct);
	}

	// What the legs collectively answered, before any fusion or ranking decision.
	readonly record struct LegResults(
		List<IReadOnlyList<string>> Rankings, Dictionary<string, Hit> ById,
		bool Lexical, bool Semantic, bool Degraded, string? Reason);

	async Task<LegResults> PollAsync(string scope, string query, SearchFilter filter, int k,
		SearchSelection selection, CancellationToken ct)
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

		return new LegResults(rankings, byId, lexical, semantic, degraded, reason);
	}

	// Fuse by rank, cap to the candidate budget, and rank the result — producing the ORDERED POOL a
	// relevance selection is served (and paged) out of.
	//
	// The fused score overwrites the per-index Score (meaningless post-fusion) and is rank-based, so it
	// stays comparable across separate SearchService calls — a consumer can still merge several
	// per-container pools by it.
	//
	// THE POOL'S CEILING is the declared rerank candidate budget (default 160, spec:
	// search-rerank-candidate-budget — an assumption, not a value derived from latency), and it now
	// applies to BOTH ranking paths rather than only the rerank one. That is deliberate: the boundary
	// a caller is told about must be ONE number, or a query that quietly degraded from Reranked to
	// DegradedRrf would also quietly change how deep the result goes. `PoolBounded` records whether
	// the candidate union actually exceeded it — the fact that separates "нет больше" from "дальше не
	// смотрели".
	async Task<SearchPool> RankPoolAsync(string scope, string query, SearchRankingMode mode,
		CandidateTextResolver? resolveCandidateText, LegResults legs,
		IReadOnlyList<(string Key, double Score)> fused, CancellationToken ct)
	{
		var poolLimit = _budget.Candidates();
		var poolBounded = fused.Count > poolLimit;
		var pool = fused.Take(poolLimit).Select(f => legs.ById[f.Key] with { Score = f.Score }).ToList();

		// PRECISION mode (spec: search-rerank-in-loop) — the штатный path when a reranker is wired. The
		// deduped candidate UNION (byId, everything the legs surfaced) is ordered by RRF, capped to the
		// declared candidate budget (so the enumerable «лексическая нога»'s full set can't flood
		// the cross-encoder), then rescored by the cross-encoder on ONE model. Reranked=true is the
		// honest provenance. Anything that goes wrong here — no rerank route, an outage, a resolver
		// failure — falls THROUGH to the RRF path below with Reranked=false: RRF is honest degradation
		// (DegradedRrf), and a rerank outage must NEVER take search down.
		if (mode == SearchRankingMode.Precision && _reranker is not null
			&& resolveCandidateText is not null && pool.Count > 0 && await IsRerankAvailableAsync(scope, ct))
		{
			try
			{
				var texts = await resolveCandidateText(pool, ct);
				if (texts.Count != pool.Count)
					throw new InvalidOperationException($"candidate-text resolver returned {texts.Count} texts for {pool.Count} candidates");
				// topN is the WHOLE pool, not some page size. The cost of a rerank is in SCORING
				// the candidates, which happens for every candidate regardless; topN only truncates what
				// comes BACK. So asking for the complete ordering
				// costs essentially nothing extra here and is precisely what makes page 2 free: the order
				// is computed once, at this line, and every later page is a slice of it.
				var reranked = await _reranker.RerankAsync(query, texts, pool.Count, ct);
				// Map the reranked GLOBAL indices back to the candidate hits, carrying the cross-encoder
				// score. Guard against an out-of-range index rather than trusting the adapter blindly.
				var ordered = reranked
					.Where(r => r.Index >= 0 && r.Index < pool.Count)
					.Select(r => pool[r.Index] with { Score = r.Score })
					.ToList();
				return new SearchPool(ordered, poolLimit, poolBounded,
					new SearchRetrievers(legs.Lexical, legs.Semantic, legs.Degraded, legs.Reason, Ranking: SearchRankingOutcome.Reranked));
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Honest RRF degradation (DegradedRrf). Log WHY so a rerank outage is visible on day one
				// instead of reading as a silent quality drop, then fall through to the RRF path.
				var code = ex is SearchDegradedException sde ? sde.Reason : ex.GetType().Name;
				if (_log is not null) LogRerankDegraded(_log, scope, code, ex);
			}
		}

		// RRF answered — provenance is honest about WHY (spec: search-rerank-in-loop /
		// search-ranking-mode-is-caller-choice): under Speed the caller CHOSE RRF (ChosenRrf); under
		// Precision we fell through because the rerank path could not run — a DEGRADATION (DegradedRrf).
		return new SearchPool(pool, poolLimit, poolBounded,
			new SearchRetrievers(legs.Lexical, legs.Semantic, legs.Degraded, legs.Reason,
				Ranking: mode == SearchRankingMode.Speed ? SearchRankingOutcome.ChosenRrf : SearchRankingOutcome.DegradedRrf));
	}

	// Fast-down gate on the precision pass: skip rerank (and its candidate-text resolution) when no
	// rerank route is live, so a project with no reranker degrades to RRF without a wasted resolve. A
	// probe that itself throws is treated as "unavailable" — it must never sink the search.
	async Task<bool> IsRerankAvailableAsync(string scope, CancellationToken ct)
	{
		try { return await _reranker!.IsAvailableAsync(ct); }
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			if (_log is not null) LogRerankDegraded(_log, scope, "rerank-probe-failed", ex);
			return false;
		}
	}

	// Fusion identity = the entity address (type, id) within the queried scope. The unit
	// separator keeps composite keys collision-free.
	static string Key(string type, string id) => type + "\x1f" + id;

	[LoggerMessage(EventId = 400, Level = LogLevel.Warning,
		Message = "search: index {Index} ({Capability}) failed in scope '{Scope}' → degraded, reason {Reason}")]
	static partial void LogIndexDegraded(ILogger logger, string index, string capability, string scope, string reason, Exception ex);

	// The precision pass fell back to RRF (spec: search-rerank-in-loop): the cross-encoder was
	// unavailable or errored, so the result is DegradedRrf (Reranked=false). Logged so a rerank outage
	// is queryable, not a silent quality drop the owner only notices by feel.
	[LoggerMessage(EventId = 401, Level = LogLevel.Warning,
		Message = "search: rerank precision pass unavailable in scope '{Scope}' → RRF degradation, reason {Reason}")]
	static partial void LogRerankDegraded(ILogger logger, string scope, string reason, Exception ex);
}
