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

	// work llm-config-capability-case-roundtrip. The two sides of the surface disagreed IN PRINT:
	// llm_config_upsert documented "embed|rerank|chat" / "enabled|disabled" lowercase, llm_config_get
	// emitted "Embed" / "Disabled". Since an upsert replaces each part it is sent, read-modify-write
	// is the only safe edit, so a case-SENSITIVE parser would have broken the one supported cycle
	// against a live level. It is not sensitive — JsonStringEnumConverter with no naming policy reads
	// through Enum.TryParse(ignoreCase: true) — and that is the fact these tests pin, in BOTH
	// directions: any casing is read, the declared member name is written.
	[Theory]
	[InlineData("embed", LlmCapability.Embed)]
	[InlineData("Embed", LlmCapability.Embed)]
	[InlineData("EMBED", LlmCapability.Embed)]
	[InlineData("rerank", LlmCapability.Rerank)]
	[InlineData("Rerank", LlmCapability.Rerank)]
	[InlineData("chat", LlmCapability.Chat)]
	[InlineData("Chat", LlmCapability.Chat)]
	public void Route_capability_parses_from_any_casing(string wire, LlmCapability expected)
	{
		var json = $$"""
			{"endpoints":[{"name":"e","baseUrl":"https://e"}],
			 "routes":[{"capability":"{{wire}}","endpoint":"e","model":"m"}]}
			""";

		JsonSerializer.Deserialize<LlmRegistry>(json, Json)!.Routes[0].Capability.Should().Be(expected);
	}

	[Theory]
	[InlineData("enabled", LlmThinking.Enabled)]
	[InlineData("Enabled", LlmThinking.Enabled)]
	[InlineData("disabled", LlmThinking.Disabled)]
	[InlineData("Disabled", LlmThinking.Disabled)]
	[InlineData("DISABLED", LlmThinking.Disabled)]
	public void Route_thinking_parses_from_any_casing(string wire, LlmThinking expected)
	{
		var json = $$"""
			{"endpoints":[{"name":"e","baseUrl":"https://e"}],
			 "routes":[{"capability":"chat","endpoint":"e","model":"m","thinking":"{{wire}}"}]}
			""";

		JsonSerializer.Deserialize<LlmRegistry>(json, Json)!.Routes[0].Thinking.Should().Be(expected);
	}

	// What makes "case-insensitive" a real finding rather than a vacuous one: an UNKNOWN value is
	// still refused. If the converter quietly fell back to the zero member, every one of the casings
	// above would have "parsed" — into Embed — and a chat route pasted back through the round trip
	// would have been silently rewritten as an embed route.
	[Theory]
	[InlineData("\"capability\":\"summarize\"")]
	[InlineData("\"capability\":\"\"")]
	public void An_unknown_capability_is_REFUSED_not_defaulted_to_the_zero_member(string capability)
	{
		var json = $$"""
			{"endpoints":[{"name":"e","baseUrl":"https://e"}],
			 "routes":[{{{capability}},"endpoint":"e","model":"m"}]}
			""";

		var act = () => JsonSerializer.Deserialize<LlmRegistry>(json, Json);

		act.Should().Throw<JsonException>();
	}

	// The WRITE side of the same fact. llm_config_get's output is Capitalized, and that is what a
	// caller pastes back — so the round trip depends on the read above staying case-insensitive.
	// If this ever changes to lowercase, the descriptions on LlmRouterTools must change with it.
	[Fact]
	public void Capability_and_thinking_serialize_as_the_declared_member_name()
	{
		var reg = new LlmRegistry(
			[new LlmEndpoint("e", "https://e")],
			[new LlmRoute(LlmCapability.Embed, "e", "m", 10, Thinking: LlmThinking.Disabled)]);

		var wire = JsonSerializer.Serialize(reg, Json);

		wire.Should().Contain("\"capability\":\"Embed\"").And.Contain("\"thinking\":\"Disabled\"");
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
