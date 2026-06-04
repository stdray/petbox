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
	Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default);

	// Cheap, non-blocking liveness check for a capability: is any provider in the chain
	// configured AND not currently circuit-broken? Lets a latency-sensitive caller decide
	// to degrade up front instead of waiting on a fallback sweep (llm-fast-down).
	Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default);
}
