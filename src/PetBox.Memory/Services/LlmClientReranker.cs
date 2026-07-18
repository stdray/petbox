using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;

namespace PetBox.Memory.Services;

// Adapts the project-routed ILlmClient to the search layer's project-agnostic IReranker, bound to one
// project (rerank routes per project's LLM config). The adapter lives at the consumer edge on purpose:
// Core.Search declares IReranker precisely so the facade never drags the LLM-router contract into Core
// (llm-consumer-decoupling), exactly as LlmClientEmbedder does for embed. It routes through
// RerankQueryAsync so the query-model AFFINITY invariant (one model for the whole pool) is honoured.
public sealed class LlmClientReranker : IReranker
{
	readonly ILlmClient _llm;
	readonly string _projectKey;
	// A pool ≤ the latency budget (~500) ships in ONE POST on the home route (measured: no provider
	// doc-cap; chunking exists only on the external fallback), so ChunkSize 0 = single call and the
	// affinity invariant holds trivially. A future larger budget can raise this without a code change.
	readonly int _chunkSize;

	public LlmClientReranker(ILlmClient llm, string projectKey, int chunkSize = 0)
	{
		_llm = llm;
		_projectKey = projectKey;
		_chunkSize = chunkSize;
	}

	public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
		_llm.IsAvailableAsync(_projectKey, LlmCapability.Rerank, ct);

	public async Task<IReadOnlyList<RerankedHit>> RerankAsync(string query, IReadOnlyList<string> documents, int topN, CancellationToken ct = default)
	{
		try
		{
			var res = await _llm.RerankQueryAsync(_projectKey, new RerankQueryRequest(query, documents, _chunkSize, topN), ct);
			return res.Hits.Select(h => new RerankedHit(h.Index, h.Score)).ToList();
		}
		catch (LlmRouterException ex)
		{
			// The edge turns a router failure into a SEARCH reason code so Core.Search never sees the
			// router's types (llm-consumer-decoupling) — the facade then degrades to RRF honestly.
			throw new SearchDegradedException(
				ex.NoRoute ? SearchDegradedReason.RerankNoRoute : SearchDegradedReason.RerankUnavailable, ex.Message, ex);
		}
	}
}
