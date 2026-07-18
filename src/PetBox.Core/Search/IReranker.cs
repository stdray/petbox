namespace PetBox.Core.Search;

// One reranked candidate: its GLOBAL index into the documents the reranker was handed, and the
// cross-encoder score on the ONE model that scored the whole pool (spec: search-rerank-single-model),
// so scores across the returned set share a single scale.
public readonly record struct RerankedHit(int Index, double Score);

// The narrow cross-encoder RERANK capability the search precision path needs — declared IN Core.Search
// so the facade never drags the LLM-router contract into Core (consumer decoupling, the same reason
// IEmbedder lives here). The wiring layer adapts the real ILlmClient.RerankQueryAsync (query-model
// affinity) to this at the consumer edge (LlmClientReranker).
//
// This is the model-only seam: it takes already-resolved document TEXT and returns a reordering. It
// knows nothing about entities — candidate-text resolution is the consumer's CandidateTextResolver,
// because the clean body lives in the consumer's entity store, not in the (stem-shadowed) FTS text.
public interface IReranker
{
	// Fast-down probe (llm-fast-down): is a rerank route configured AND not currently circuit-broken?
	// Lets the facade skip the precision pass — and its candidate-text resolution — up front, degrading
	// to RRF (DegradedRrf) WITHOUT a wasted resolve or a thrown exception when rerank is simply not
	// available here.
	Task<bool> IsAvailableAsync(CancellationToken ct = default);

	// Rerank `documents` for `query` with query-model AFFINITY (ONE model scores the WHOLE pool, spec
	// search-rerank-single-model), returning the best-first reordered GLOBAL indices into `documents`
	// with the cross-encoder score, truncated to topN. Throws SearchDegradedException on a rerank
	// outage/no-route so the facade falls back to RRF honestly (reranked=false) — an outage must never
	// take search down.
	Task<IReadOnlyList<RerankedHit>> RerankAsync(string query, IReadOnlyList<string> documents, int topN, CancellationToken ct = default);
}

// Resolves each candidate hit's DOCUMENT TEXT for the rerank pass — supplied by the consumer, which
// alone knows how to fetch an entity's body. Given the budget-capped candidates in fused order, it
// returns their texts ALIGNED by index (same count, same order); an unresolved hit yields "" and simply
// scores low. Kept a per-call delegate rather than an index method ON PURPOSE: the clean body lives in
// the consumer's entity store, and the FTS text carries appended stem shadow terms that would be noise
// in a cross-encoder input.
public delegate Task<IReadOnlyList<string>> CandidateTextResolver(IReadOnlyList<Hit> candidates, CancellationToken ct);
