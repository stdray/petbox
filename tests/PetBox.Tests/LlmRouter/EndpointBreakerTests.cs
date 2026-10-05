using Microsoft.Extensions.Time.Testing;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using PetBox.LlmRouter.Routing;

namespace PetBox.Tests.LlmRouter;

// The circuit breaker that makes a dead endpoint fail fast (llm-fast-down), with the SCOPE of each
// failure decided by its class (work llmrouter-breaker-scope-per-capability).
//
// The two halves matter equally and pull in opposite directions, so both are pinned here:
// an UNREACHABLE host suppresses the whole endpoint (that is the feature — a sleeping `home` must
// not cost a connect-timeout on every call, whichever capability is calling), while a 429 or a 5xx
// suppresses only the leg that hit it (the provider answered; it is alive).
public sealed class EndpointBreakerTests
{
	[Fact]
	public void Opens_after_threshold_then_half_opens_after_cooldown()
	{
		var time = new FakeTimeProvider();
		var b = new EndpointBreaker(time) { FailureThreshold = 2, OpenDuration = TimeSpan.FromSeconds(30) };

		b.IsOpen("a", LlmCapability.Chat).Should().BeFalse();
		b.RecordFailure("a", LlmCapability.Chat, LlmFailureClass.Unreachable);
		b.IsOpen("a", LlmCapability.Chat).Should().BeFalse("one failure is below the threshold");
		b.RecordFailure("a", LlmCapability.Chat, LlmFailureClass.Unreachable);
		b.IsOpen("a", LlmCapability.Chat).Should().BeTrue("threshold reached -> open");

		time.Advance(TimeSpan.FromSeconds(29));
		b.IsOpen("a", LlmCapability.Chat).Should().BeTrue("still within the cooldown");
		time.Advance(TimeSpan.FromSeconds(2));
		b.IsOpen("a", LlmCapability.Chat).Should().BeFalse("cooldown elapsed -> half-open, let the next attempt through");
	}

	[Fact]
	public void Success_resets_the_failure_count()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 2 };

		b.RecordFailure("a", LlmCapability.Chat, LlmFailureClass.Unreachable);
		b.RecordSuccess("a", LlmCapability.Chat);
		b.RecordFailure("a", LlmCapability.Chat, LlmFailureClass.Unreachable);
		b.IsOpen("a", LlmCapability.Chat).Should().BeFalse("the success reset the counter, so one new failure is below threshold");
	}

	[Fact]
	public void Tracks_endpoints_independently()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };
		b.RecordFailure("a", LlmCapability.Chat, LlmFailureClass.Unreachable);
		b.IsOpen("a", LlmCapability.Chat).Should().BeTrue();
		b.IsOpen("b", LlmCapability.Chat).Should().BeFalse("a failure on 'a' must not open 'b'");
	}

	// ---- the scope split ----

	// The regression this change exists for. OpenRouter's free tier is a PER-ENDPOINT budget, so a
	// Chat 429 is the normal way this endpoint goes transient — and it must not decide where the
	// next Embed vector comes from.
	[Fact]
	public void A_throttled_chat_leg_does_not_open_the_endpoint_for_embed_or_rerank()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };

		b.RecordFailure("openrouter", LlmCapability.Chat, LlmFailureClass.Throttled);

		b.IsOpen("openrouter", LlmCapability.Chat).Should().BeTrue("the leg that was throttled is skipped");
		b.IsOpen("openrouter", LlmCapability.Embed).Should().BeFalse("a chat rate limit must not divert embedding traffic");
		b.IsOpen("openrouter", LlmCapability.Rerank).Should().BeFalse("nor reranking traffic");
	}

	// A 5xx is the provider ANSWERING, so it is leg-scoped for the same reason a 429 is.
	[Fact]
	public void A_server_error_is_leg_scoped_too()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };

		b.RecordFailure("openrouter", LlmCapability.Rerank, LlmFailureClass.ServerError);

		b.IsOpen("openrouter", LlmCapability.Rerank).Should().BeTrue();
		b.IsOpen("openrouter", LlmCapability.Embed).Should().BeFalse("one model 500-ing is not the host being down");
	}

	// llm-fast-down, preserved: a host that does not answer at all is skipped for EVERY capability,
	// so the sleeping-home case costs one connect-timeout, not one per call.
	[Fact]
	public void An_unreachable_host_is_skipped_for_every_capability()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };

		b.RecordFailure("home", LlmCapability.Chat, LlmFailureClass.Unreachable);

		foreach (var cap in Enum.GetValues<LlmCapability>())
			b.IsOpen("home", cap).Should().BeTrue($"{cap} must not pay a connect-timeout to an asleep host");
		b.IsOpen("openrouter", LlmCapability.Chat).Should().BeFalse("and it is still only this endpoint");
	}

	// An unknown class is treated as endpoint-wide — the expensive direction to be wrong in for a
	// sleeping host is the one that keeps paying timeouts, not the one that retries one leg.
	[Fact]
	public void An_unknown_failure_class_is_treated_as_unreachable()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };

		b.RecordFailure("home", LlmCapability.Chat);

		b.IsOpen("home", LlmCapability.Embed).Should().BeTrue();
	}

	// A definitive refusal (4xx, oversize, unparseable) never counts — there is nothing to protect
	// against, the next leg is simply tried.
	[Fact]
	public void A_definitive_refusal_never_opens_anything()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };

		b.RecordFailure("openrouter", LlmCapability.Chat, LlmFailureClass.None);

		b.IsOpen("openrouter", LlmCapability.Chat).Should().BeFalse();
	}

	// A success clears the endpoint AND this leg, but not another capability's leg state: a Chat
	// success says nothing about an Embed leg that is out of quota.
	[Fact]
	public void A_success_does_not_clear_another_capabilitys_leg()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };
		b.RecordFailure("openrouter", LlmCapability.Chat, LlmFailureClass.Throttled);
		b.RecordFailure("openrouter", LlmCapability.Embed, LlmFailureClass.Throttled);

		b.RecordSuccess("openrouter", LlmCapability.Chat);

		b.IsOpen("openrouter", LlmCapability.Chat).Should().BeFalse("its own leg is healthy again");
		b.IsOpen("openrouter", LlmCapability.Embed).Should().BeTrue("a different leg's throttle survives it");
	}

	// The remaining cooldown is what the skip is logged with, so "it will be back in a moment" is a
	// fact in the log rather than a guess.
	[Fact]
	public void The_skip_reports_its_scope_and_counts_down()
	{
		var time = new FakeTimeProvider();
		var b = new EndpointBreaker(time) { FailureThreshold = 1, OpenDuration = TimeSpan.FromSeconds(30) };
		b.RecordFailure("a", LlmCapability.Chat, LlmFailureClass.Throttled);

		b.OpenReason("a", LlmCapability.Chat)!.Value.Scope.Should().Be(EndpointBreaker.SkipScope.Leg);
		b.OpenReason("a", LlmCapability.Chat)!.Value.Remaining.Should().Be(TimeSpan.FromSeconds(30));

		b.RecordFailure("b", LlmCapability.Chat, LlmFailureClass.Unreachable);
		b.OpenReason("b", LlmCapability.Chat)!.Value.Scope.Should().Be(EndpointBreaker.SkipScope.Endpoint);

		time.Advance(TimeSpan.FromSeconds(10));
		b.OpenReason("a", LlmCapability.Chat)!.Value.Remaining.Should().Be(TimeSpan.FromSeconds(20));

		time.Advance(TimeSpan.FromSeconds(25));
		b.OpenReason("a", LlmCapability.Chat).Should().BeNull("past the cooldown -> attemptable");
	}

	[Fact]
	public void A_leg_that_never_failed_is_not_open()
	{
		new EndpointBreaker(new FakeTimeProvider())
			.OpenReason("nobody", LlmCapability.Embed).Should().BeNull();
	}
}
