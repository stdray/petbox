using System.Text.Json;
using PetBox.LlmRouter.Contract;

namespace PetBox.Tests.LlmRouter;

// The registry round-trips through Web-default JSON in two places (config binding storage and
// the llm_config_upsert/get MCP surface); `thinking` must survive both and parse from the
// lowercase wire form (llm-route-reasoning-mode).
public sealed class LlmRegistryJsonTests
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	[Fact]
	public void Route_thinking_roundtrips_and_defaults_to_null()
	{
		var reg = new LlmRegistry(
			[new LlmEndpoint("ds", "https://d")],
			[
				new LlmRoute(LlmCapability.Chat, "ds", "deepseek-v4-pro", 10, Thinking: LlmThinking.Disabled),
				new LlmRoute(LlmCapability.Embed, "ds", "qwen3-embed-4b", 10),
			]);

		var parsed = JsonSerializer.Deserialize<LlmRegistry>(JsonSerializer.Serialize(reg, Json), Json)!;

		parsed.Routes[0].Thinking.Should().Be(LlmThinking.Disabled);
		parsed.Routes[1].Thinking.Should().BeNull();
	}

	[Fact]
	public void Route_thinking_parses_lowercase_wire_form()
	{
		const string json = """
			{"endpoints":[{"name":"ds","baseUrl":"https://d"}],
			 "routes":[{"capability":"chat","endpoint":"ds","model":"m","thinking":"disabled"}]}
			""";

		var parsed = JsonSerializer.Deserialize<LlmRegistry>(json, Json)!;

		parsed.Routes[0].Thinking.Should().Be(LlmThinking.Disabled);
	}

	// llm-embed-space-id: embedSpaceId is the config-surface field for the shared vector-index key.
	// It must survive the llm_config_upsert -> llm_config_get JSON round-trip and default to null.
	[Fact]
	public void Route_embed_space_id_roundtrips_and_defaults_to_null()
	{
		var reg = new LlmRegistry(
			[new LlmEndpoint("home", "https://h"), new LlmEndpoint("openrouter", "https://o")],
			[
				new LlmRoute(LlmCapability.Embed, "home", "qwen3-embed-4b", 10, EmbedSpaceId: "qwen3-embed-4b-space"),
				new LlmRoute(LlmCapability.Embed, "openrouter", "qwen/qwen3-embedding-4b", 20, EmbedSpaceId: "qwen3-embed-4b-space"),
				new LlmRoute(LlmCapability.Chat, "home", "deepseek-v4-pro", 10),
			]);

		var parsed = JsonSerializer.Deserialize<LlmRegistry>(JsonSerializer.Serialize(reg, Json), Json)!;

		parsed.Routes[0].EmbedSpaceId.Should().Be("qwen3-embed-4b-space");
		parsed.Routes[1].EmbedSpaceId.Should().Be("qwen3-embed-4b-space", "both providers declare one shared space");
		parsed.Routes[1].Model.Should().Be("qwen/qwen3-embedding-4b", "the provider model string is independent of the space key");
		parsed.Routes[2].EmbedSpaceId.Should().BeNull("a route that declares no space defaults to null");
	}

	// The wire form is camelCase and optional: a route JSON without embedSpaceId parses to null
	// (backward compatibility — old config payloads have no such field).
	[Fact]
	public void Route_without_embed_space_id_parses_null()
	{
		const string json = """
			{"endpoints":[{"name":"h","baseUrl":"https://h"}],
			 "routes":[{"capability":"embed","endpoint":"h","model":"qwen3-embed-4b"}]}
			""";

		var parsed = JsonSerializer.Deserialize<LlmRegistry>(json, Json)!;

		parsed.Routes[0].EmbedSpaceId.Should().BeNull();
	}
}
