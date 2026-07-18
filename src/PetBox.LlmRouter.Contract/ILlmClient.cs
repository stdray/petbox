namespace PetBox.LlmRouter.Contract;

// The neutral LLM capability contract every consumer depends on — NOT the router impl.
// Swapping the provider (local llama-server <-> a cloud OpenAI-compatible endpoint, or a
// direct in-process client) is a DI change, never a change in consumer code
// (llm-consumer-decoupling). All calls are project-scoped; the router resolves the
// project's configured endpoint/route chain (llm-config-driven).
public interface ILlmClient
{
	Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default);
	Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default);

	// Query-affinity rerank over a candidate pool that MAY exceed one provider call (spec:
	// search-rerank-single-model): ONE SEARCH QUERY = ONE MODEL FOR ALL ITS CHUNKS. The DEFAULT
	// implementation is the degenerate single-POST rerank — one call, one model, so the affinity
	// invariant holds trivially and a client that never chunks (or a test fake) needs no change. A
	// router that chunks a large pool OVERRIDES this to score every chunk on the same route's model
	// and fall back whole-query (CapabilityRouter.RerankQueryAsync).
	Task<RerankResult> RerankQueryAsync(string projectKey, RerankQueryRequest request, CancellationToken ct = default) =>
		RerankAsync(projectKey, new RerankRequest(request.Query, request.Documents, request.TopN, request.Tier), ct);

	Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default);

	// Cheap, non-blocking liveness check for a capability: is any provider in the chain
	// configured AND not currently circuit-broken? Lets a latency-sensitive caller decide
	// to degrade up front instead of waiting on a fallback sweep (llm-fast-down).
	Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default);
}
