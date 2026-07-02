using System.Text.Json;
using PetBox.LlmRouter.Contract;

namespace PetBox.Tests.LlmRouter;

// The registry round-trips through Web-default JSON in two places (config binding storage and
// the llm_config_set/get MCP surface); `thinking` must survive both and parse from the
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
}
