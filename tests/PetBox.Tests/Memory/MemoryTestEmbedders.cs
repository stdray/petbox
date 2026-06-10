using PetBox.LlmRouter.Contract;

namespace PetBox.Tests.Memory;

// Deterministic embedders shared by the memory search tests. FakeLlmClient derives a vector from a
// stable text hash so the same text always embeds to the same point; a sentinel (NearQueryMarker)
// or any query-like input collapses to the query vector so semantic-only hits are reproducible.
public sealed class FakeLlmClient : ILlmClient
{
	public const int Dim = 8;
	public const string Model = "fake-embed-v1";
	public const string NearQueryMarker = "__NEARQUERY__";

	public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default)
	{
		var vectors = request.Inputs.Select(Vector).ToList();
		return Task.FromResult(new EmbedResult(vectors, new ModelIdentity(Model, Dim),
			new ServedBy("fake", Model, 1, Degraded: false)));
	}

	static float[] Vector(string text)
	{
		// Any text carrying the marker (and any query) collapses to the same unit vector,
		// so marked documents sit adjacent to the query embedding.
		if (text.Contains(NearQueryMarker) || !text.Contains(' ') || IsQueryLike(text))
		{
			var q = new float[Dim];
			q[0] = 1f;
			return q;
		}
		var v = new float[Dim];
		var h = unchecked((uint)text.GetHashCode());
		for (var i = 0; i < Dim; i++)
		{
			v[i] = ((h >> i) & 1) == 1 ? 1f : -1f;
			h = h * 2654435761u + 1u;
		}
		return v;
	}

	// Heuristic: short, single-token inputs are treated as queries and map to the
	// query vector — keeps the semantic leg deterministic for the test queries used.
	static bool IsQueryLike(string text) => !text.Contains('\n') && text.Split(' ').Length <= 2;

	public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
		throw new NotSupportedException();
	public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
		throw new NotSupportedException();
	public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
		Task.FromResult(true);
}

// Embedder whose every call throws — exercises the degrade/dead-letter paths.
public sealed class ThrowingLlmClient : ILlmClient
{
	public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
		throw new InvalidOperationException("embed down");
	public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
		throw new NotSupportedException();
	public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
		throw new NotSupportedException();
	public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
		Task.FromResult(true);
}
