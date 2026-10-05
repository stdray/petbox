using System.Collections.Concurrent;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;

namespace PetBox.LlmRouter.Routing;

// A circuit breaker that makes a dead endpoint fail fast (llm-fast-down), with the SCOPE of each
// failure decided by its class (work llmrouter-breaker-scope-per-capability).
//
// Two scopes, because the two situations need opposite behaviour:
//
//   * ENDPOINT-WIDE — for `LlmFailureClass.Unreachable` (connect refused, DNS, TLS, timeout). The
//     host is down or asleep and that is true for EVERY capability pointed at it, so suppressing the
//     whole endpoint is precisely the win this type exists for: a sleeping `home` must not cost a
//     connect-timeout on every later call, whichever capability is calling. This is the ORIGINAL
//     behaviour, kept on purpose.
//   * LEG-WIDE (endpoint + capability) — for `LlmFailureClass.Throttled` (429) and `ServerError`
//     (5xx). The provider ANSWERED, so the endpoint is alive and only a quota, or that one leg, is
//     spent. OpenRouter's free tier is a per-endPOINT budget, so an endpoint-wide breaker on a 429
//     moves Embed and Rerank traffic for a problem only Chat had.
//
// Measured on production 2026-10-05, and the reason this split exists: two Chat 429s on `openrouter`
// (`free-models-per-min`) opened the endpoint for 30 s and Embed + Rerank were served by the `home`
// fallback in that window, with no log line anywhere near it to explain why.
//
// A leg is skipped when EITHER scope is open, and the more specific reason is reported — "throttled"
// and "unreachable" are different facts for whoever reads the log.
public sealed class EndpointBreaker
{
	public int FailureThreshold { get; init; } = 2;
	public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);

	readonly TimeProvider _time;
	readonly ConcurrentDictionary<string, State> _endpoints = new(StringComparer.Ordinal);
	readonly ConcurrentDictionary<LegKey, State> _legs = new();

	public EndpointBreaker(TimeProvider time) => _time = time;

	// One leg of one capability's chain. A record struct so two legs can never collide by accident
	// (an endpoint named "Chat" and a capability are different things and must not share state).
	readonly record struct LegKey(string Endpoint, LlmCapability Capability);

	sealed class State
	{
		public int ConsecutiveFailures;
		public DateTimeOffset? OpenUntil;
	}

	/// <summary>Why a leg is being skipped.</summary>
	public enum SkipScope
	{
		/// <summary>The whole endpoint looks unreachable — skipped for every capability.</summary>
		Endpoint,

		/// <summary>The endpoint answered; only this capability's leg is throttled or erroring.</summary>
		Leg,
	}

	public readonly record struct Skip(SkipScope Scope, TimeSpan Remaining);

	public bool IsOpen(string endpoint, LlmCapability capability) => OpenReason(endpoint, capability) is not null;

	/// <summary>
	/// Why this leg would be skipped and for how much longer, or <c>null</c> when it may be attempted.
	/// Reading this has the half-open side effect: once a cooldown has elapsed the state is cleared
	/// and the next call goes through.
	/// </summary>
	public Skip? OpenReason(string endpoint, LlmCapability capability)
	{
		if (Remaining(_endpoints, endpoint) is { } ep && ep > TimeSpan.Zero) return new Skip(SkipScope.Endpoint, ep);
		if (Remaining(_legs, new LegKey(endpoint, capability)) is { } leg && leg > TimeSpan.Zero) return new Skip(SkipScope.Leg, leg);
		return null;
	}

	/// <summary>
	/// Record a leg's outcome. An `Unreachable` failure opens the endpoint for everyone; the other
	/// classes open only this leg. <c>null</c> means "class unknown" and is treated as endpoint-wide —
	/// for a sleeping host the expensive mistake is the one that keeps paying timeouts.
	/// </summary>
	public void RecordFailure(string endpoint, LlmCapability capability, LlmFailureClass? failureClass = null)
	{
		if (failureClass is null or LlmFailureClass.Unreachable)
			Bump(_endpoints, endpoint);
		else if (failureClass != LlmFailureClass.None)
			Bump(_legs, new LegKey(endpoint, capability));
	}

	/// <summary>
	/// A success proves the endpoint is alive (getting an answer is what it needed) and that this leg
	/// is healthy, so it clears both. It deliberately does NOT clear ANOTHER capability's leg state:
	/// a Chat success says nothing about an Embed leg that is out of quota.
	/// </summary>
	public void RecordSuccess(string endpoint, LlmCapability capability)
	{
		_endpoints.TryRemove(endpoint, out _);
		_legs.TryRemove(new LegKey(endpoint, capability), out _);
	}

	void Bump<TKey>(ConcurrentDictionary<TKey, State> map, TKey key) where TKey : notnull
	{
		var s = map.GetOrAdd(key, _ => new State());
		lock (s)
		{
			s.ConsecutiveFailures++;
			if (s.ConsecutiveFailures >= FailureThreshold) s.OpenUntil = _time.GetUtcNow() + OpenDuration;
		}
	}

	TimeSpan? Remaining<TKey>(ConcurrentDictionary<TKey, State> map, TKey key) where TKey : notnull
	{
		if (!map.TryGetValue(key, out var s) || s.OpenUntil is null) return null;
		return s.OpenUntil.Value - _time.GetUtcNow();
	}
}
