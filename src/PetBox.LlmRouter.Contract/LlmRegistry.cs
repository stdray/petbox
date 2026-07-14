using System.Text.Json.Serialization;

namespace PetBox.LlmRouter.Contract;

// The reasoning ("thinking") mode a chat route requests from its model. Null on the route =
// don't send anything, the provider's default applies. Matters because providers flip the
// default per model name (DeepSeek v4 explicit names think by default and max_tokens covers
// reasoning + answer, so a small budget can return empty content).
[JsonConverter(typeof(JsonStringEnumConverter<LlmThinking>))]
public enum LlmThinking
{
	Enabled,
	Disabled,
}

// A reachable OpenAI-compatible endpoint. The api key is NOT stored here — it lives as an
// encrypted secret binding keyed by Name and is resolved at call time (llm-endpoint-security).
// CertThumbprint is the SHA-256 fingerprint to pin for a self-signed endpoint (null = trust
// the public CA chain). ConnectTimeoutMs is kept short so an unreachable endpoint fails fast
// instead of hanging (llm-fast-down).
public sealed record LlmEndpoint(
	string Name,
	string BaseUrl,
	string? CertThumbprint = null,
	int ConnectTimeoutMs = 2000,
	int RequestTimeoutMs = 60000);

// One link in a capability's ordered provider chain: which endpoint + upstream model serves
// a (capability[, tier]) at what priority (lower = tried first). A route with a null Tier is
// the default and serves any requested tier (llm-fallback-chain). Thinking declares the
// model's reasoning mode for chat routes (llm-route-reasoning-mode); null = provider default.
//
// EmbedSpaceId is EMBED-ONLY and it is the KEY OF THE VECTOR INDEX — the canonical name every
// vector produced by this route is stored and searched under, decoupled from Model. Model is what
// goes to the provider as the API parameter; EmbedSpaceId is what the index compares on. Null =
// fall back to Model (backward compatible: an existing index keyed by the home model name stays
// valid, no reindex). Two embed routes that name the SAME EmbedSpaceId (e.g. a home model and an
// OpenRouter fallback whose provider model strings differ) declare their vectors to live in ONE
// space and therefore be mutually comparable. Ignored for Chat/Rerank (their identity is always
// Model). Last parameter on purpose: keeps every positional LlmRoute(...) call source-compatible.
public sealed record LlmRoute(
	LlmCapability Capability,
	string Endpoint,
	string Model,
	int Priority = 100,
	string? Tier = null,
	LlmThinking? Thinking = null,
	string? EmbedSpaceId = null);

// The full router registry: the endpoints and the routes that order them per capability.
// Persisted as JSON in the Config module (llm-config-driven) — configurable, not hardcoded.
public sealed record LlmRegistry(
	IReadOnlyList<LlmEndpoint> Endpoints,
	IReadOnlyList<LlmRoute> Routes)
{
	public static LlmRegistry Empty { get; } = new([], []);
}
