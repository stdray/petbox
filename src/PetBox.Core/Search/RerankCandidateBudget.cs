namespace PetBox.Core.Search;

// The rerank CANDIDATE BUDGET — how many candidates a search query is allowed to carry into a
// (future) cross-encoder rerank pass (spec: search-rerank-candidate-budget). Its whole point is
// that the number is DERIVED from the latency bar and the MEASURED per-document cost of the real
// rerank route — NOT a relevance intuition, NOT a "generous top-K" constant picked by feel.
//
// MEASURED (2026-07-18, warm, home route qwen3-rerank-0.6b, whole list one POST):
//   n=100 ~0.95s · n=200 ~1.6-1.9s · n=300 ~2.25-2.4s · n=500 ~3.4-4.0s · n=750 ~4.9-5.5s (5s
//   bar breaks here) · n=1000 ~6.5-7.1s. Linear fit: ~6.1 ms/doc over a ~0.31-0.35s base (the
//   owner's ~0.535s base, warm p95 ~1.0-1.2s @ n=100, both reproduced). The local reranker
//   accepted up to 8000 docs in ONE call with NO error, NO chunking, NO degradation — so on this
//   route the binding constraint is LATENCY, not a provider doc-cap. Chunking (and the quota it
//   multiplies) only exists on the external fallback, which was not exercised here.
//
// So the budget is (LatencyBarMs − BaseMs) / PerDocMs, with headroom: at the 5s bar the raw
// ceiling is ~770 docs (min) / ~700 (p95), and ~500 keeps the MEASURED p95 (max 3.99s @ n=500)
// comfortably under 5s. This budget is the VECTOR leg's top-K only: the «лексическая нога» is
// enumerable (it returns everything the facet predicate leaves, it has NO top-K), so the budget
// never caps it — a generous top-K on the lexical leg is exactly the defect this must not carry.
public sealed record RerankCandidateBudget
{
	// The owner's latency bar for the whole search response (spec decision: 5 seconds).
	public double LatencyBarMs { get; init; } = 5000;

	// Measured per-document marginal cost and fixed per-call base of the real rerank route (warm).
	// These are the empirical slope/intercept above — change them ONLY behind a fresh measurement.
	public double PerDocMs { get; init; } = 6.1;
	public double BaseMs { get; init; } = 350;

	// Fraction of the raw latency ceiling kept as budget, so warm p95 (not just the min) stays
	// under the bar. 0.65 puts the budget at ~500 candidates against the measured curve — the
	// "wide pool with several-fold headroom" the owner described, not the anonymous SearchK=50.
	public double HeadroomFraction { get; init; } = 0.65;

	// The derived budget: how many candidates fit under the latency bar, with headroom. Never a
	// stored constant — recompute it whenever the route or the bar changes and re-measure PerDocMs.
	public int Candidates()
	{
		var rawCeiling = (LatencyBarMs - BaseMs) / PerDocMs;   // docs that fit at the hard bar
		var budget = (int)System.Math.Floor(rawCeiling * HeadroomFraction);
		return budget < 1 ? 1 : budget;
	}
}
