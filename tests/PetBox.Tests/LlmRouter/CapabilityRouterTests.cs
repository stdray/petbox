using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using PetBox.LlmRouter.Registry;
using PetBox.LlmRouter.Routing;

namespace PetBox.Tests.LlmRouter;

// The fallback walk (llm-fallback-chain + llm-fast-down): transient failures fall through to
// the next provider, a non-transient failure is surfaced without masking, an exhausted chain
// throws transient, and a circuit-open endpoint is skipped without an attempt.
public sealed class CapabilityRouterTests
{
	// One resolved LEVEL (the router now consumes ILlmRegistryLevelResolver, not the old store).
	static ResolvedRegistryLevel Level(LlmRegistry registry, bool inheritanceBlocked = false) =>
		new(RegistryLevel.System, registry, new Dictionary<string, string>(StringComparer.Ordinal),
			inheritanceBlocked, "proj", "ws");

	// Two embed providers, primary (priority 10) then secondary (priority 20).
	static ResolvedRegistryLevel TwoEmbed() => Level(
		new LlmRegistry(
			[new LlmEndpoint("primary", "https://p"), new LlmEndpoint("secondary", "https://s")],
			[
				new LlmRoute(LlmCapability.Embed, "primary", "mp", 10),
				new LlmRoute(LlmCapability.Embed, "secondary", "ms", 20),
			]));

	// The same two providers, but both declaring ONE shared embedding space "home-space". Their
	// provider Model strings still differ ("mp" vs "ms") — that is the whole point: two providers,
	// one index key.
	static ResolvedRegistryLevel TwoEmbedSharedSpace() => Level(
		new LlmRegistry(
			[new LlmEndpoint("primary", "https://p"), new LlmEndpoint("secondary", "https://s")],
			[
				new LlmRoute(LlmCapability.Embed, "primary", "mp", 10, EmbedSpaceId: "home-space"),
				new LlmRoute(LlmCapability.Embed, "secondary", "ms", 20, EmbedSpaceId: "home-space"),
			]));

	static CapabilityRouter Build(ILlmRegistryLevelResolver resolver, IOpenAiCompatibleClient upstream, EndpointBreaker breaker) =>
		new(resolver, new CertPinningHttpClientProvider(), upstream, breaker, NullLogger<CapabilityRouter>.Instance);

	[Fact]
	public async Task Falls_back_to_secondary_on_transient_failure()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "connection refused");
		upstream.EmbedBehaviour["https://s"] = () => [[1f, 2f, 3f]];
		var breaker = new EndpointBreaker(new FakeTimeProvider());
		var router = Build(new FakeResolver(TwoEmbed()), upstream, breaker);

		var res = await router.EmbedAsync("proj", new EmbedRequest(["hello"]));

		res.ServedBy.Endpoint.Should().Be("secondary");
		res.ServedBy.AttemptCount.Should().Be(2);
		res.Vectors.Should().ContainSingle();
		res.Model.Dim.Should().Be(3);
		upstream.EmbedCalls.Should().Equal("https://p", "https://s");
	}

	[Fact]
	public async Task Non_transient_failure_is_not_masked_by_fallback()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(false, "400 bad request");
		upstream.EmbedBehaviour["https://s"] = () => [[9f]];
		var router = Build(new FakeResolver(TwoEmbed()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var act = async () => await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		(await act.Should().ThrowAsync<LlmRouterException>()).Which.Transient.Should().BeFalse();
		upstream.EmbedCalls.Should().Equal("https://p");
	}

	[Fact]
	public async Task All_providers_transient_throws_exhausted_transient()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "down");
		upstream.EmbedBehaviour["https://s"] = () => throw new LlmUpstreamException(true, "down");
		var router = Build(new FakeResolver(TwoEmbed()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var act = async () => await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		(await act.Should().ThrowAsync<LlmRouterException>()).Which.Transient.Should().BeTrue();
		upstream.EmbedCalls.Should().Equal("https://p", "https://s");
	}

	[Fact]
	public async Task Open_circuit_endpoint_is_skipped_without_attempt()
	{
		var upstream = new FakeUpstream();
		// primary not registered -> would throw KeyNotFound if (wrongly) attempted.
		upstream.EmbedBehaviour["https://s"] = () => [[1f]];
		var breaker = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };
		breaker.RecordFailure("primary"); // open it (threshold 1)
		var router = Build(new FakeResolver(TwoEmbed()), upstream, breaker);

		var res = await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		res.ServedBy.Endpoint.Should().Be("secondary");
		res.ServedBy.AttemptCount.Should().Be(1, "the open primary was skipped, not attempted");
		upstream.EmbedCalls.Should().Equal("https://s");
	}

	// ---- embed-space identity (llm-embed-space-id): the vector-index key is decoupled from the
	// provider Model. Two routes sharing an EmbedSpaceId must yield the SAME identity whichever one
	// serves, so both providers' vectors are comparable in the index. ----

	[Fact]
	public async Task Embed_identity_is_the_shared_space_when_primary_serves()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => [[1f, 2f, 3f]];
		var router = Build(new FakeResolver(TwoEmbedSharedSpace()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		res.ServedBy.Endpoint.Should().Be("primary");
		res.ServedBy.UpstreamModel.Should().Be("mp", "the provider is still called with its own model string");
		res.Model.Model.Should().Be("home-space", "the index is keyed by the shared space, not the provider model");
		res.Model.Dim.Should().Be(3);
	}

	[Fact]
	public async Task Embed_identity_is_the_same_shared_space_after_fallback_to_secondary()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "down");
		upstream.EmbedBehaviour["https://s"] = () => [[4f, 5f, 6f]];
		var router = Build(new FakeResolver(TwoEmbedSharedSpace()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		res.ServedBy.Endpoint.Should().Be("secondary");
		res.ServedBy.UpstreamModel.Should().Be("ms", "the fallback provider is called with ITS own model string");
		// The load-bearing assertion: fallback to a different provider produced the SAME index key as
		// the primary would have — so vectors from both providers live in one comparable space.
		res.Model.Model.Should().Be("home-space");
	}

	[Fact]
	public async Task Embed_identity_falls_back_to_provider_model_when_no_space_declared()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "down");
		upstream.EmbedBehaviour["https://s"] = () => [[7f]];
		// TwoEmbed() declares NO EmbedSpaceId — the backward-compatible default. Identity == provider Model,
		// exactly as before this feature, so the existing index (keyed by the home model name) stays valid.
		var router = Build(new FakeResolver(TwoEmbed()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		res.ServedBy.Endpoint.Should().Be("secondary");
		res.Model.Model.Should().Be("ms", "null EmbedSpaceId means the identity is the served route's Model");
	}

	[Fact]
	public async Task Rerank_identity_is_the_provider_model_unchanged()
	{
		var reg = Level(
			new LlmRegistry(
				[new LlmEndpoint("rr", "https://r")],
				[new LlmRoute(LlmCapability.Rerank, "rr", "reranker-v1", 10)]));
		var upstream = new FakeUpstream { RerankReply = [new RerankHit(0, 0.9)] };
		var router = Build(new FakeResolver(reg), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.RerankAsync("proj", new RerankRequest("q", ["d"]));

		res.Model.Model.Should().Be("reranker-v1", "rerank identity is the provider model — EmbedSpaceId is embed-only");
	}

	[Fact]
	public async Task Chat_identity_is_the_provider_model_unchanged()
	{
		var reg = Level(
			new LlmRegistry(
				[new LlmEndpoint("ds", "https://d")],
				[new LlmRoute(LlmCapability.Chat, "ds", "chat-v4", 10)]));
		var upstream = new FakeUpstream { ChatReply = "ok" };
		var router = Build(new FakeResolver(reg), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));

		res.Model.Model.Should().Be("chat-v4", "chat identity is the provider model — EmbedSpaceId is embed-only");
	}

	[Fact]
	public async Task Chat_passes_route_thinking_to_upstream()
	{
		var reg = Level(
			new LlmRegistry(
				[new LlmEndpoint("ds", "https://d")],
				[new LlmRoute(LlmCapability.Chat, "ds", "m", 10, Thinking: LlmThinking.Disabled)]));
		var upstream = new FakeUpstream { ChatReply = "ok" };
		var router = Build(new FakeResolver(reg), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));

		res.Text.Should().Be("ok");
		upstream.ChatThinking.Should().Equal(LlmThinking.Disabled);
	}

	[Fact]
	public async Task Chat_without_thinking_passes_null()
	{
		var reg = Level(
			new LlmRegistry(
				[new LlmEndpoint("ds", "https://d")],
				[new LlmRoute(LlmCapability.Chat, "ds", "m", 10)]));
		var upstream = new FakeUpstream { ChatReply = "ok" };
		var router = Build(new FakeResolver(reg), upstream, new EndpointBreaker(new FakeTimeProvider()));

		await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));

		upstream.ChatThinking.Should().Equal((LlmThinking?)null);
	}

	[Fact]
	public async Task No_route_for_capability_throws_non_transient()
	{
		var resolved = new ResolvedRegistryLevel(null, LlmRegistry.Empty,
			new Dictionary<string, string>(StringComparer.Ordinal), InheritanceBlocked: false, "proj", "ws");
		var router = Build(new FakeResolver(resolved), new FakeUpstream(), new EndpointBreaker(new FakeTimeProvider()));

		var act = async () => await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		var ex = (await act.Should().ThrowAsync<LlmRouterException>()).Which;
		ex.Transient.Should().BeFalse();
		ex.NoRoute.Should().BeTrue();
		// The message is the resolver's honest one, not a generic "no route configured".
		ex.Message.Should().Contain("no route for Embed").And.Contain("ws");
	}

	// ---- fakes ----

	sealed class FakeResolver(ResolvedRegistryLevel reg) : ILlmRegistryLevelResolver
	{
		public Task<ResolvedRegistryLevel> ResolveAsync(string projectKey, CancellationToken ct = default) => Task.FromResult(reg);
	}

	sealed class FakeUpstream : IOpenAiCompatibleClient
	{
		public Dictionary<string, Func<IReadOnlyList<float[]>>> EmbedBehaviour { get; } = new(StringComparer.Ordinal);
		public List<string> EmbedCalls { get; } = [];
		public string ChatReply { get; init; } = "";
		public List<LlmThinking?> ChatThinking { get; } = [];
		public IReadOnlyList<RerankHit> RerankReply { get; init; } = [];

		public Task<IReadOnlyList<float[]>> EmbedAsync(HttpClient http, string baseUrl, string? apiKey, string model, IReadOnlyList<string> inputs, CancellationToken ct)
		{
			EmbedCalls.Add(baseUrl);
			return Task.FromResult(EmbedBehaviour[baseUrl]());
		}

		public Task<IReadOnlyList<RerankHit>> RerankAsync(HttpClient http, string baseUrl, string? apiKey, string model, string query, IReadOnlyList<string> documents, int? topN, CancellationToken ct) =>
			Task.FromResult(RerankReply);

		public Task<string> ChatAsync(HttpClient http, string baseUrl, string? apiKey, string model, IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens, LlmThinking? thinking, CancellationToken ct)
		{
			ChatThinking.Add(thinking);
			return Task.FromResult(ChatReply);
		}
	}
}
