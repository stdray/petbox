using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;

namespace PetBox.Memory.Services;

// Adapts the project-routed ILlmClient to the search layer's project-agnostic IEmbedder, bound to
// one project (embeddings route per project's LLM config). The adapter lives at the consumer edge
// on purpose: Core.Search declares IEmbedder precisely so a Class-B vector index never drags the
// LLM-router contract into Core (llm-consumer-decoupling, m-b3fbe908). Constructed per scope by the
// memory read path and the vectorization worker; cheap, stateless.
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
		var res = await _llm.EmbedAsync(_projectKey, new EmbedRequest(inputs), ct);
		var dim = res.Vectors.Count > 0 ? res.Vectors[0].Length : 0;
		return new EmbedBatch(res.Vectors, res.Model.Model, dim);
	}
}
