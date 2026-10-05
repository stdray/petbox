using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using PetBox.LlmRouter.Registry;
using PetBox.LlmRouter.Routing;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PetBox.Tests.LlmRouter;

// The fallback walk (llm-fallback-chain + llm-fast-down): a transient OR non-transient failure
// both fall through to the next provider (route-chain-aborts-on-size-refusal, decided
// 2026-08-28 — the chain never aborts early any more), a circuit-open endpoint is skipped
// without an attempt, and an exhausted chain's exception carries what happened on every leg.
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

	// Two rerank providers, home (priority 10) then fallback (priority 20) — for the query-level
	// affinity walk (spec: search-rerank-single-model).
	static ResolvedRegistryLevel TwoRerank() => Level(
		new LlmRegistry(
			[new LlmEndpoint("primary", "https://p"), new LlmEndpoint("secondary", "https://s")],
			[
				new LlmRoute(LlmCapability.Rerank, "primary", "home-rr", 10),
				new LlmRoute(LlmCapability.Rerank, "secondary", "fallback-rr", 20),
			]));

	// Chat and Embed over the SAME two endpoints. That shared endpoint is what makes the
	// per-endpoint breaker a cross-capability hazard: one capability's failures used to suppress
	// the other (work llmrouter-breaker-scope-per-capability).
	static ResolvedRegistryLevel ChatAndEmbed() => Level(
		new LlmRegistry(
			[new LlmEndpoint("primary", "https://p"), new LlmEndpoint("secondary", "https://s")],
			[
				new LlmRoute(LlmCapability.Chat, "primary", "chat-p", 10),
				new LlmRoute(LlmCapability.Chat, "secondary", "chat-s", 20),
				new LlmRoute(LlmCapability.Embed, "primary", "mp", 10),
				new LlmRoute(LlmCapability.Embed, "secondary", "ms", 20),
			]));

	static CapabilityRouter Build(ILlmRegistryLevelResolver resolver, IOpenAiCompatibleClient upstream, EndpointBreaker breaker) =>
		new(resolver, new CertPinningHttpClientProvider(), upstream, breaker, NullLogger<CapabilityRouter>.Instance);

	// Same, with the router's logger captured — a skip that changes WHERE a call is served is a
	// routing event and must be findable in the log at the production minimum level.
	static CapabilityRouter Build(ILlmRegistryLevelResolver resolver, IOpenAiCompatibleClient upstream,
		EndpointBreaker breaker, ILogger<CapabilityRouter> logger) =>
		new(resolver, new CertPinningHttpClientProvider(), upstream, breaker, logger);


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

	// THE red-before-fix case (route-chain-aborts-on-size-refusal, owner decision 2026-08-28): a
	// non-transient failure on the first leg must NOT stop the walk — the second leg still gets
	// tried, and its success is what the caller sees. Before the fix this threw LlmRouterException
	// instead of returning the secondary's result.
	[Fact]
	public async Task Non_transient_failure_falls_through_to_the_next_provider()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(false, "400 bad request");
		upstream.EmbedBehaviour["https://s"] = () => [[9f]];
		var router = Build(new FakeResolver(TwoEmbed()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		res.ServedBy.Endpoint.Should().Be("secondary");
		res.ServedBy.AttemptCount.Should().Be(2);
		res.Vectors.Should().ContainSingle();
		upstream.EmbedCalls.Should().Equal("https://p", "https://s");
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

	// When ALL legs fail, the exception must let a reader see what happened on EACH one — not
	// just the last leg attempted. Mixed failure kinds (non-transient then transient) also drive
	// the exhaustion Transient flag: false, because not every leg failed transiently.
	[Fact]
	public async Task All_providers_failing_reports_every_leg_not_just_the_last()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(false, "400 bad request on primary");
		upstream.EmbedBehaviour["https://s"] = () => throw new LlmUpstreamException(true, "timeout on secondary");
		var router = Build(new FakeResolver(TwoEmbed()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var act = async () => await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		var ex = (await act.Should().ThrowAsync<LlmRouterException>()).Which;
		ex.Transient.Should().BeFalse("at least one leg (primary) failed non-transiently");
		ex.Message.Should().Contain("primary").And.Contain("400 bad request on primary");
		ex.Message.Should().Contain("secondary").And.Contain("timeout on secondary");
		upstream.EmbedCalls.Should().Equal("https://p", "https://s");
		ex.InnerException.Should().BeOfType<AggregateException>()
			.Which.InnerExceptions.Should().HaveCount(2, "both legs' original exceptions are preserved");
	}

	[Fact]
	public async Task Open_circuit_endpoint_is_skipped_without_attempt()
	{
		var upstream = new FakeUpstream();
		// primary not registered -> would throw KeyNotFound if (wrongly) attempted.
		upstream.EmbedBehaviour["https://s"] = () => [[1f]];
		var breaker = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };
		breaker.RecordFailure("primary", LlmCapability.Embed); // open it (threshold 1)
		var router = Build(new FakeResolver(TwoEmbed()), upstream, breaker);

		var res = await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		res.ServedBy.Endpoint.Should().Be("secondary");
		res.ServedBy.AttemptCount.Should().Be(1, "the open primary was skipped, not attempted");
		upstream.EmbedCalls.Should().Equal("https://s");
	}

	// ---- breaker scope (work llmrouter-breaker-scope-per-capability) ----
	//
	// Production measurement that motivated the change: on 2026-10-05 two Chat 429s
	// (OpenRouter `free-models-per-min`, a PER-ENDPOINT budget) tripped the breaker for `openrouter`
	// and held it 30 s, and Embed + Rerank traffic was served by the `home` fallback in that window
	// with nothing in the log to explain it. A provider rate limit must not decide where the next
	// embedding comes from.

	[Fact]
	public async Task A_chat_breaker_does_not_divert_the_embed_chain()
	{
		var upstream = new FakeUpstream { ChatReply = "from secondary" };
		// A 429 — the provider answered, only Chat's budget is spent.
		upstream.ChatBehaviour["https://p"] = () => new LlmUpstreamException(true, "HTTP 429", rateLimited: true);
		upstream.EmbedBehaviour["https://p"] = () => [[1f]];
		upstream.EmbedBehaviour["https://s"] = () => throw new LlmUpstreamException(true, "secondary down");
		var breaker = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };
		var router = Build(new FakeResolver(ChatAndEmbed()), upstream, breaker);

		// Chat fails on the primary leg -> its breaker opens for Chat only.
		var chat = await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));
		chat.ServedBy.Endpoint.Should().Be("secondary");

		// Embed on the SAME endpoints must still try the primary first.
		var embed = await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		embed.ServedBy.Endpoint.Should().Be("primary", "a chat RATE LIMIT must not move embedding traffic");
		embed.ServedBy.AttemptCount.Should().Be(1);
	}

	[Fact]
	public async Task An_embed_breaker_does_not_divert_the_chat_chain()
	{
		var upstream = new FakeUpstream { ChatReply = "ok" };
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "HTTP 429", rateLimited: true);
		upstream.EmbedBehaviour["https://s"] = () => [[1f]];
		var breaker = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };
		var router = Build(new FakeResolver(ChatAndEmbed()), upstream, breaker);

		await router.EmbedAsync("proj", new EmbedRequest(["x"]));
		var chat = await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));

		chat.ServedBy.Endpoint.Should().Be("primary", "an embed 429 must not move chat traffic");
		chat.ServedBy.AttemptCount.Should().Be(1);
	}

	// llm-fast-down, preserved end to end. An UNREACHABLE host must still be skipped for every
	// capability — that is the feature the breaker exists for: a sleeping `home` costs one
	// connect-timeout, not one per call. The scoping change must not have narrowed this away.
	[Fact]
	public async Task An_unreachable_endpoint_is_still_skipped_for_every_capability()
	{
		var upstream = new FakeUpstream { ChatReply = "ok" };
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "connection failed: refused");
		upstream.ChatBehaviour["https://p"] = () => new LlmUpstreamException(true, "connection failed: refused");
		upstream.EmbedBehaviour["https://s"] = () => [[1f]];
		upstream.ChatBehaviour["https://s"] = () => (Exception?)null;
		var breaker = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };
		var router = Build(new FakeResolver(ChatAndEmbed()), upstream, breaker);

		// Embed trips the endpoint-wide breaker on the connect failure...
		var embed = await router.EmbedAsync("proj", new EmbedRequest(["x"]));
		embed.ServedBy.Endpoint.Should().Be("secondary");

		// ...and chat on the SAME endpoint must then be skipped too, not pay another connect.
		var chat = await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));

		chat.ServedBy.Endpoint.Should().Be("secondary");
		chat.ServedBy.AttemptCount.Should().Be(1, "the asleep host was skipped, not attempted");
	}

	// The two skip kinds are different facts and must not be logged as one event: "the host is
	// unreachable" and "this capability is out of quota" lead to different operator actions.
	[Fact]
	public async Task Throttled_and_unreachable_skips_are_logged_as_different_events()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "HTTP 429", rateLimited: true);
		upstream.EmbedBehaviour["https://s"] = () => [[1f]];
		var breaker = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1, OpenDuration = TimeSpan.FromSeconds(42) };
		var log = new CapturingLogger<CapabilityRouter>();
		var router = Build(new FakeResolver(TwoEmbed()), upstream, breaker, log);

		await router.EmbedAsync("proj", new EmbedRequest(["x"]));   // primary 429s, secondary serves
		upstream.EmbedBehaviour["https://p"] = () => [[1f]];        // would now succeed — but is skipped
		await router.EmbedAsync("proj", new EmbedRequest(["x"]));

		var entry = log.Entries.Should().ContainSingle(e => e.EventId == 307).Subject;
		entry.Level.Should().Be(MsLogLevel.Information, "Debug is invisible in production — that is part of the bug");
		entry.Message.Should().Contain("primary").And.Contain("Embed").And.Contain("42").And.Contain("throttled");
	}

	[Fact]
	public async Task An_unreachable_skip_is_logged_as_endpoint_wide()
	{
		var upstream = new FakeUpstream();
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "connection failed: refused");
		upstream.EmbedBehaviour["https://s"] = () => [[1f]];
		var breaker = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1, OpenDuration = TimeSpan.FromSeconds(30) };
		var log = new CapturingLogger<CapabilityRouter>();
		var router = Build(new FakeResolver(TwoEmbed()), upstream, breaker, log);

		await router.EmbedAsync("proj", new EmbedRequest(["x"]));   // primary unreachable, secondary serves
		await router.EmbedAsync("proj", new EmbedRequest(["x"]));   // primary now skipped endpoint-wide

		var entry = log.Entries.Should().ContainSingle(e => e.EventId == 301).Subject;
		entry.Message.Should().Contain("UNREACHABLE").And.Contain("all capabilities").And.Contain("primary");
	}

	[Fact]
	public async Task The_exhaustion_message_still_names_a_skipped_open_leg()
	{
		var upstream = new FakeUpstream(); // every embed throws below
		upstream.EmbedBehaviour["https://p"] = () => throw new LlmUpstreamException(true, "down");
		upstream.EmbedBehaviour["https://s"] = () => throw new LlmUpstreamException(true, "down");
		var breaker = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1, OpenDuration = TimeSpan.FromSeconds(30) };
		var router = Build(new FakeResolver(TwoEmbed()), upstream, breaker);

		// The first call fails on both legs (that is the point — it is what trips both breakers).
		await Assert.ThrowsAsync<LlmRouterException>(() => router.EmbedAsync("proj", new EmbedRequest(["x"])));

		var act = async () => await router.EmbedAsync("proj", new EmbedRequest(["x"]));
		var ex = (await act.Should().ThrowAsync<LlmRouterException>()).Which;
		ex.Message.Should().Contain("endpoint unreachable", "a skipped leg is still reported to the caller");
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

	// ---- query-level rerank AFFINITY (search-rerank-single-model): one query = one model for ALL
	// its chunks; fallback is whole-query, never a per-chunk "as it comes" mix (two scales). ----

	[Fact]
	public async Task RerankQuery_scores_every_chunk_on_one_model()
	{
		var upstream = new FakeUpstream();
		// home answers every chunk; one hit per doc, local index preserved so the remap is visible.
		upstream.RerankBehaviour["https://p"] = docs => docs.Select((_, i) => new RerankHit(i, 1.0 - i * 0.01)).ToList();
		var router = Build(new FakeResolver(TwoRerank()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.RerankQueryAsync("proj",
			new RerankQueryRequest("q", ["d0", "d1", "d2", "d3", "d4"], ChunkSize: 2));

		res.ServedBy.Endpoint.Should().Be("primary");
		res.Model.Model.Should().Be("home-rr");
		// 5 docs / chunk 2 → three chunks, EVERY one on home — the fallback model never touched a chunk.
		upstream.RerankCalls.Select(c => c.BaseUrl).Should().OnlyContain(u => u == "https://p");
		upstream.RerankCalls.Select(c => c.DocCount).Should().Equal(2, 2, 1);
		// Hit indices are GLOBAL positions across chunks, not per-chunk offsets.
		res.Hits.Select(h => h.Index).Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4 });
	}

	[Fact]
	public async Task RerankQuery_falls_back_whole_query_never_mixing_models()
	{
		var upstream = new FakeUpstream();
		// home SUCCEEDS on the first chunk but THROWS transient on the chunk carrying "d3".
		upstream.RerankBehaviour["https://p"] = docs =>
			docs.Contains("d3") ? throw new LlmUpstreamException(true, "home blip")
				: docs.Select((_, i) => new RerankHit(i, 0.5)).ToList();
		upstream.RerankBehaviour["https://s"] = docs => docs.Select((_, i) => new RerankHit(i, 0.9 - i * 0.01)).ToList();
		var router = Build(new FakeResolver(TwoRerank()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.RerankQueryAsync("proj",
			new RerankQueryRequest("q", ["d0", "d1", "d2", "d3"], ChunkSize: 2));

		res.ServedBy.Endpoint.Should().Be("secondary");
		res.ServedBy.AttemptCount.Should().Be(2);
		res.Model.Model.Should().Be("fallback-rr");
		// The load-bearing assertion: home scored chunk0 then threw on chunk1 → the WHOLE route was
		// abandoned and the fallback replayed BOTH chunks. Call order proves whole-query replay, and
		// the 4-hit result proves home's chunk0 partial was DISCARDED (a mix would be 6 hits / 2 scales).
		upstream.RerankCalls.Select(c => c.BaseUrl).Should().Equal("https://p", "https://p", "https://s", "https://s");
		res.Hits.Should().HaveCount(4);
		res.Hits.Select(h => h.Index).Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
	}

	// Same red-before-fix invariant as EmbedAsync, on the chunked query-affinity path: a
	// non-transient failure on home's FIRST chunk must not abort the whole query — the fallback
	// route still gets tried (whole-query replay, never a per-chunk mix).
	[Fact]
	public async Task RerankQuery_non_transient_failure_falls_through_to_the_next_route()
	{
		var upstream = new FakeUpstream();
		upstream.RerankBehaviour["https://p"] = _ => throw new LlmUpstreamException(false, "413 payload too large");
		upstream.RerankBehaviour["https://s"] = docs => docs.Select((_, i) => new RerankHit(i, 0.9 - i * 0.01)).ToList();
		var router = Build(new FakeResolver(TwoRerank()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.RerankQueryAsync("proj",
			new RerankQueryRequest("q", ["d0", "d1"], ChunkSize: 10));

		res.ServedBy.Endpoint.Should().Be("secondary");
		res.Model.Model.Should().Be("fallback-rr");
		upstream.RerankCalls.Select(c => c.BaseUrl).Should().Equal("https://p", "https://s");
	}

	[Fact]
	public async Task RerankQuery_topN_is_applied_across_the_whole_pool_after_merge()
	{
		var upstream = new FakeUpstream();
		// score = 10 − the doc's number, so d0 is the strongest globally; one model scores all.
		upstream.RerankBehaviour["https://p"] = docs => docs.Select((d, i) => new RerankHit(i, 10 - int.Parse(d[1..]))).ToList();
		var router = Build(new FakeResolver(TwoRerank()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.RerankQueryAsync("proj",
			new RerankQueryRequest("q", ["d0", "d1", "d2", "d3"], ChunkSize: 2, TopN: 2));

		// Two chunks, yet TopN spans the merged pool: the top 2 are d0,d1 — NOT one-per-chunk (d0,d2),
		// which is what a per-chunk topN would wrongly return.
		res.Hits.Should().HaveCount(2);
		res.Hits.Select(h => h.Index).Should().Equal(0, 1);
	}

	[Fact]
	public async Task RerankQuery_is_a_single_call_when_the_pool_fits_one_chunk()
	{
		var upstream = new FakeUpstream();
		upstream.RerankBehaviour["https://p"] = docs => docs.Select((_, i) => new RerankHit(i, 1.0)).ToList();
		var router = Build(new FakeResolver(TwoRerank()), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.RerankQueryAsync("proj",
			new RerankQueryRequest("q", ["d0", "d1"], ChunkSize: 10));

		// The degenerate single-POST form (today's behaviour) is a special case of the same path.
		upstream.RerankCalls.Should().ContainSingle().Which.DocCount.Should().Be(2);
		res.ServedBy.Endpoint.Should().Be("primary");
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

	// work llmrouter-reasoning-param-passthrough: the router must carry the route's reasoning
	// control all the way to the upstream call, exactly as it does for thinking — a field that
	// stops at the registry fixes nothing.
	[Fact]
	public async Task Chat_passes_route_reasoning_to_upstream()
	{
		var reg = Level(
			new LlmRegistry(
				[new LlmEndpoint("openrouter", "https://o")],
				[new LlmRoute(LlmCapability.Chat, "openrouter", "ling-free", 10, Reasoning: LlmReasoningEffort.None)]));
		var upstream = new FakeUpstream { ChatReply = "ok" };
		var router = Build(new FakeResolver(reg), upstream, new EndpointBreaker(new FakeTimeProvider()));

		var res = await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));

		res.Text.Should().Be("ok");
		upstream.ChatReasoning.Should().Equal(LlmReasoningEffort.None);
		upstream.ChatThinking.Should().Equal((LlmThinking?)null); // reasoning does not imply thinking
	}

	[Fact]
	public async Task Chat_without_reasoning_passes_null()
	{
		var reg = Level(
			new LlmRegistry(
				[new LlmEndpoint("openrouter", "https://o")],
				[new LlmRoute(LlmCapability.Chat, "openrouter", "m", 10)]));
		var upstream = new FakeUpstream { ChatReply = "ok" };
		var router = Build(new FakeResolver(reg), upstream, new EndpointBreaker(new FakeTimeProvider()));

		await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));

		upstream.ChatReasoning.Should().Equal((LlmReasoningEffort?)null);
	}

	[Fact]
	public async Task Chat_passes_request_response_format_to_upstream()
	{
		var reg = Level(
			new LlmRegistry(
				[new LlmEndpoint("ds", "https://d")],
				[new LlmRoute(LlmCapability.Chat, "ds", "m", 10)]));
		var upstream = new FakeUpstream { ChatReply = "ok" };
		var router = Build(new FakeResolver(reg), upstream, new EndpointBreaker(new FakeTimeProvider()));

		await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")],
			ResponseFormat: LlmResponseFormat.JsonObject.Instance));

		upstream.ChatResponseFormats.Should().Equal(LlmResponseFormat.JsonObject.Instance);
	}

	[Fact]
	public async Task Chat_without_response_format_passes_null()
	{
		var reg = Level(
			new LlmRegistry(
				[new LlmEndpoint("ds", "https://d")],
				[new LlmRoute(LlmCapability.Chat, "ds", "m", 10)]));
		var upstream = new FakeUpstream { ChatReply = "ok" };
		var router = Build(new FakeResolver(reg), upstream, new EndpointBreaker(new FakeTimeProvider()));

		await router.ChatAsync("proj", new ChatRequest([new ChatMessage("user", "hi")]));

		upstream.ChatResponseFormats.Should().Equal((LlmResponseFormat?)null);
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
		ex.Capability.Should().Be(LlmCapability.Embed, "the exception already carries this typed — no need to grep the message for it");
		// The message is the resolver's honest one, not a generic "no route configured".
		ex.Message.Should().Contain("ws");
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
		public List<LlmReasoningEffort?> ChatReasoning { get; } = [];
		public List<LlmResponseFormat?> ChatResponseFormats { get; } = [];
		public IReadOnlyList<RerankHit> RerankReply { get; init; } = [];
		// Per-endpoint rerank behaviour (keyed by baseUrl) — a func of the chunk's documents so a
		// fake can score by content or THROW; when absent, RerankReply is the default. RerankCalls
		// records (baseUrl, docCount) in order so a test can prove which model scored which chunk.
		public Dictionary<string, Func<IReadOnlyList<string>, IReadOnlyList<RerankHit>>> RerankBehaviour { get; } = new(StringComparer.Ordinal);
		public List<(string BaseUrl, int DocCount)> RerankCalls { get; } = [];

		public Task<IReadOnlyList<float[]>> EmbedAsync(HttpClient http, string baseUrl, string? apiKey, string model, IReadOnlyList<string> inputs, CancellationToken ct)
		{
			EmbedCalls.Add(baseUrl);
			return Task.FromResult(EmbedBehaviour[baseUrl]());
		}

		public Task<IReadOnlyList<RerankHit>> RerankAsync(HttpClient http, string baseUrl, string? apiKey, string model, string query, IReadOnlyList<string> documents, int? topN, CancellationToken ct)
		{
			RerankCalls.Add((baseUrl, documents.Count));
			return Task.FromResult(RerankBehaviour.TryGetValue(baseUrl, out var f) ? f(documents) : RerankReply);
		}

		// Per-endpoint chat failure (keyed by baseUrl), so "primary is down, secondary answers" is
		// expressible — the shape that trips one leg's breaker without failing the whole chain.
		public Dictionary<string, Func<Exception?>> ChatBehaviour { get; } = new(StringComparer.Ordinal);

		public Task<string> ChatAsync(HttpClient http, string baseUrl, string? apiKey, string model, IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens, LlmThinking? thinking, LlmReasoningEffort? reasoning, LlmResponseFormat? responseFormat, CancellationToken ct)
		{
			ChatThinking.Add(thinking);
			ChatReasoning.Add(reasoning);
			ChatResponseFormats.Add(responseFormat);
			if (ChatBehaviour.TryGetValue(baseUrl, out var fail) && fail() is { } err)
				return Task.FromException<string>(err);
			return Task.FromResult(ChatReply);
		}
	}

	sealed record LogEntry(MsLogLevel Level, int EventId, string Message);

	sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<LogEntry> Entries { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
	}
}
