using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;

namespace PetBox.Tasks.Services;

// Adapts the project-routed ILlmClient to the search layer's project-agnostic IEmbedder, bound to
// one project. Lives at the consumer edge (Core.Search declares IEmbedder so a Class-B vector index
// never drags the LLM-router contract into Core). Constructed per scope by the read path and the
// vectorization worker; cheap, stateless. (Mirrors the memory module's adapter — kept per-module to
// avoid a cross-module reference for ~12 lines.)
public sealed class LlmClientEmbedder : IEmbedder
{
	readonly ILlmClient _llm;
	readonly string _projectKey;

	public LlmClientEmbedder(ILlmClient llm, string projectKey)
	{
		_llm = llm;
		_projectKey = projectKey;
	}

	public async Task<EmbedBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
	{
		try
		{
			var res = await _llm.EmbedAsync(_projectKey, new EmbedRequest(inputs), ct);
			var dim = res.Vectors.Count > 0 ? res.Vectors[0].Length : 0;
			return new EmbedBatch(res.Vectors, res.Model.Model, dim);
		}
		catch (LlmRouterException ex)
		{
			// The edge is also where the router's failure is TRANSLATED into a search reason code,
			// so the facade can report WHY it degraded without Core knowing the router exists.
			throw new SearchDegradedException(SearchDegradedReason.Embed(ex.NoRoute, ex.Transient, ex.RateLimited), ex.Message, ex);
		}
	}
}
