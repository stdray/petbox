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

// The OpenRouter-dialect REASONING control, as one value. OpenRouter normalizes every provider's
// reasoning switch into a single `reasoning` object on the chat request:
//
//   "reasoning": { "effort": "none" | "minimal" | "low" | "medium" | "high" | "xhigh" | "max" }
//
// `none` disables reasoning outright; every other level allocates a share of `max_tokens` to it
// (roughly 10% for minimal … 95% for max). Null on the route = send no `reasoning` field at all,
// which leaves the PROVIDER DEFAULT in force — and for most reasoning models that default is ON
// (`/api/v1/models` reports `reasoning.default_enabled: true` for e.g.
// inclusionai/ling-3.0-flash-sante:free), so "unset" is not "off".
//
// WHY THIS EXISTS ALONGSIDE LlmThinking. LlmThinking is the DeepSeek dialect
// (`thinking: {type: enabled|disabled}`) and is what our routes have always carried; on a
// DeepSeek endpoint it works. It is NOT understood by OpenRouter, and a measured trial on
// inclusionai/ling-3.0-flash-sante:free showed `thinking: disabled` changing nothing: at
// `max_tokens: 64` that route returned empty content on 4/6 calls with the flag and 5/8 without.
// Because reasoning tokens are billed against `max_tokens`, a reasoning model asked for a small
// budget can spend ALL of it reasoning and answer with `content: ""` and
// `finish_reason: "length"` — which our client cannot distinguish from a real empty answer. See
// the observations board node llm-chat-free-reasoning-model-empty-text-small-max-tokens.
//
// Deliberately NOT modelled here: `exclude` (hide reasoning from the response — it does not stop
// reasoning and does not fix the budget problem above), a reasoning TOKEN BUDGET
// (`reasoning.max_tokens`, supported only by Gemini/Anthropic/some Qwen-thinking models), and
// `enabled: false` (an inference of `effort`/`max_tokens`, i.e. the same wire shape as `none`).
// Add them when a route needs them, not before.
[JsonConverter(typeof(JsonStringEnumConverter<LlmReasoningEffort>))]
public enum LlmReasoningEffort
{
	None,
	Minimal,
	Low,
	Medium,
	High,
	XHigh,
	Max,
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
// Model). Both trailing parameters stay last on purpose: every positional LlmRoute(...) call
// keeps compiling.
public sealed record LlmRoute(
	LlmCapability Capability,
	string Endpoint,
	string Model,
	int Priority = 100,
	string? Tier = null,
	LlmThinking? Thinking = null,
	string? EmbedSpaceId = null,

	// CHAT-ONLY, and INDEPENDENT of Thinking: the two are different provider dialects for the same
	// wish, and a route may carry either or both (never sent together for one provider — see
	// OpenAiCompatibleClient, which renders each in its own dialect).
	LlmReasoningEffort? Reasoning = null);

// The full router registry: the endpoints and the routes that order them per capability.
// Persisted as JSON in the Config module (llm-config-driven) — configurable, not hardcoded.
public sealed record LlmRegistry(
	IReadOnlyList<LlmEndpoint> Endpoints,
	IReadOnlyList<LlmRoute> Routes)
{
	public static LlmRegistry Empty { get; } = new([], []);
}
