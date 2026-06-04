using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Http;

// The single upstream call surface: one OpenAI-compatible HTTP client used for ALL three
// capabilities (llama-server, OpenRouter, DeepSeek all speak this dialect). Abstracted as an
// interface so the router's fallback logic can be unit-tested with a fake that fails the
// first endpoint and succeeds the next, with no network. Each method gets the per-endpoint
// HttpClient (cert-pinned, short connect-timeout) chosen by the router. Throws
// LlmUpstreamException to signal transient-vs-fatal.
public interface IOpenAiCompatibleClient
{
	Task<IReadOnlyList<float[]>> EmbedAsync(
		HttpClient http, string baseUrl, string? apiKey, string model,
		IReadOnlyList<string> inputs, CancellationToken ct);

	Task<IReadOnlyList<RerankHit>> RerankAsync(
		HttpClient http, string baseUrl, string? apiKey, string model,
		string query, IReadOnlyList<string> documents, int? topN, CancellationToken ct);

	Task<string> ChatAsync(
		HttpClient http, string baseUrl, string? apiKey, string model,
		IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens, CancellationToken ct);
}
