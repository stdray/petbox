using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;

namespace PetBox.Tasks.Services;

// Adapts the project-routed ILlmClient to the search layer's project-agnostic IReranker, bound to one
// project. Consumer-edge adapter (llm-consumer-decoupling), mirroring LlmClientEmbedder: Core.Search
// declares IReranker so the facade never depends on the LLM-router contract. Routes through
// RerankQueryAsync so the query-model AFFINITY invariant (one model for the whole pool) is honoured.
public sealed class LlmClientReranker : IReranker
{
	readonly ILlmClient _llm;
	readonly string _projectKey;
	// A pool ≤ the latency budget (~500) ships in ONE POST on the home route (measured), so ChunkSize
	// 0 = single call and affinity holds trivially. Raise only behind a larger budget + a re-measure.
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
			throw new SearchDegradedException(
				ex.NoRoute ? SearchDegradedReason.RerankNoRoute : SearchDegradedReason.RerankUnavailable, ex.Message, ex);
		}
	}
}
