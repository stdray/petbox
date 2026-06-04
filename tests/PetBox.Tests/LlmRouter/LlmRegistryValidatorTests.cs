using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Registry;

namespace PetBox.Tests.LlmRouter;

// Registry validation (llm-config-driven): a route may only point at a declared endpoint,
// URLs must be absolute http(s), endpoint names unique.
public sealed class LlmRegistryValidatorTests
{
	static readonly LlmRegistryValidator V = new();

	[Fact]
	public void Valid_registry_passes()
	{
		var reg = new LlmRegistry(
			[new LlmEndpoint("local", "https://host:1234")],
			[new LlmRoute(LlmCapability.Embed, "local", "qwen3-embed-4b")]);
		V.Validate(reg).IsValid.Should().BeTrue();
	}

	[Fact]
	public void Route_to_unknown_endpoint_fails()
	{
		var reg = new LlmRegistry(
			[new LlmEndpoint("local", "https://host:1234")],
			[new LlmRoute(LlmCapability.Embed, "missing", "m")]);
		var r = V.Validate(reg);
		r.IsValid.Should().BeFalse();
		r.Errors.Should().Contain(e => e.ErrorMessage.Contains("unknown endpoint", StringComparison.Ordinal));
	}

	[Fact]
	public void Non_absolute_url_fails()
	{
		var reg = new LlmRegistry([new LlmEndpoint("local", "not-a-url")], []);
		V.Validate(reg).IsValid.Should().BeFalse();
	}

	[Fact]
	public void Duplicate_endpoint_names_fail()
	{
		var reg = new LlmRegistry(
			[new LlmEndpoint("x", "https://a"), new LlmEndpoint("x", "https://b")], []);
		V.Validate(reg).IsValid.Should().BeFalse();
	}

	[Fact]
	public void Empty_registry_is_valid()
	{
		V.Validate(LlmRegistry.Empty).IsValid.Should().BeTrue();
	}
}
