using Microsoft.Extensions.Time.Testing;
using PetBox.LlmRouter.Routing;

namespace PetBox.Tests.LlmRouter;

// The circuit breaker that makes a dead endpoint fail fast (llm-fast-down): opens after a
// threshold of consecutive failures, stays open for a cooldown, then half-opens.
public sealed class EndpointBreakerTests
{
	[Fact]
	public void Opens_after_threshold_then_half_opens_after_cooldown()
	{
		var time = new FakeTimeProvider();
		var b = new EndpointBreaker(time) { FailureThreshold = 2, OpenDuration = TimeSpan.FromSeconds(30) };

		b.IsOpen("a").Should().BeFalse();
		b.RecordFailure("a");
		b.IsOpen("a").Should().BeFalse("one failure is below the threshold");
		b.RecordFailure("a");
		b.IsOpen("a").Should().BeTrue("threshold reached -> open");

		time.Advance(TimeSpan.FromSeconds(29));
		b.IsOpen("a").Should().BeTrue("still within the cooldown");
		time.Advance(TimeSpan.FromSeconds(2));
		b.IsOpen("a").Should().BeFalse("cooldown elapsed -> half-open, let the next attempt through");
	}

	[Fact]
	public void Success_resets_the_failure_count()
	{
		var time = new FakeTimeProvider();
		var b = new EndpointBreaker(time) { FailureThreshold = 2 };

		b.RecordFailure("a");
		b.RecordSuccess("a");
		b.RecordFailure("a");
		b.IsOpen("a").Should().BeFalse("the success reset the counter, so one new failure is below threshold");
	}

	[Fact]
	public void Tracks_endpoints_independently()
	{
		var b = new EndpointBreaker(new FakeTimeProvider()) { FailureThreshold = 1 };
		b.RecordFailure("a");
		b.IsOpen("a").Should().BeTrue();
		b.IsOpen("b").Should().BeFalse("a failure on 'a' must not open 'b'");
	}
}
