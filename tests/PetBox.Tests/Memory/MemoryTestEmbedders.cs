using PetBox.LlmRouter.Contract;

namespace PetBox.Tests.Memory;

// Deterministic embedders shared by the memory search tests. FakeLlmClient derives a vector from a
// stable text hash so the same text always embeds to the same point; a sentinel (NearQueryMarker)
// or any query-like input collapses to the query vector so semantic-only hits are reproducible.
public sealed class FakeLlmClient : ILlmClient
{
	const int Dim = 8;
	const string Model = "fake-embed-v1";
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

// Usage-telemetry sink for adapter-signature tests that don't assert counters.
public sealed class NoopUsageRecorder : PetBox.Memory.Contract.IMemoryUsageRecorder
{
	public void Surfaced(string projectKey, string store, IReadOnlyList<string> keys, bool deliberate = true) { }
	public void Opened(string projectKey, string store, string key) { }
	public void Delivered(string projectKey, IReadOnlyList<PetBox.Memory.Contract.MemoryDeliveryEvent> events) { }
	public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// An embedder that can be taken DOWN mid-test without touching any data. It exists for the pagination
// regression that nothing else could express: an Embed outage between two pages is a change in
// AVAILABILITY, not in data, so every data-derived stamp (and therefore the pool cache key and the
// cursor fingerprint) still matches — which is exactly the window in which a page must not quietly
// lose the rows only the vector leg could surface. Embeds delegate to FakeLlmClient while up.
public sealed class FlakyLlmClient : ILlmClient
{
	readonly FakeLlmClient _inner = new();

	// Flip to true to simulate a transient Embed outage. Nothing else about the world changes.
	public bool EmbedDown { get; set; }

	public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
		EmbedDown ? throw new InvalidOperationException("embed down") : _inner.EmbedAsync(projectKey, request, ct);

	// A WORKING rerank route, order-preserving. It has to work for the pool cache to engage at all:
	// a pool that fell back to RRF is never stored (there is no reranker pass to save, and keeping it
	// would only pin stale provenance), so a test about caching needs a rerank that actually ran.
	public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default)
	{
		RerankCalls++;
		IReadOnlyList<RerankHit> hits = [.. request.Documents
			.Select((_, i) => new RerankHit(i, 1.0 / (i + 1)))
			.Take(request.TopN ?? request.Documents.Count)];
		return Task.FromResult(new RerankResult(hits, new ModelIdentity("fake-rerank", 0),
			new ServedBy("fake", "fake-rerank", 1, Degraded: false)));
	}

	// How many cross-encoder passes actually ran — the number requirement 5 is about.
	public int RerankCalls { get; private set; }

	public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
		throw new NotSupportedException();

	// Availability tracks the outage flag for BOTH capabilities: a route that cannot embed is also the
	// route the rerank pass would use, and the point of the flag is one coherent outage, not a mixture.
	public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
		Task.FromResult(!EmbedDown);
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
