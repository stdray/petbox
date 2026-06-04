using System.Text.Json.Serialization;

namespace PetBox.LlmRouter.Contract;

// The kind of LLM work a route serves. Each capability has its own ordered provider
// chain (llm-capabilities). rerank is first-class from v1 (it already runs locally).
// Serialized as a string everywhere (config JSON + MCP in/out) for readability.
[JsonConverter(typeof(JsonStringEnumConverter<LlmCapability>))]
public enum LlmCapability
{
	Embed,
	Rerank,
	Chat,
}
