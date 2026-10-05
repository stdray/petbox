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

		/// <summary>
		/// Has anything already reported this opening? Reset every time the breaker opens, so the
		/// caller can log the TRANSITION once and keep quiet for the rest of the cooldown — a busy
		/// endpoint otherwise emits one Information event per skipped request for 30 s.
		/// </summary>
		public bool Announced;
	}

	/// <summary>Why a leg is being skipped.</summary>
	public enum SkipScope
	{
		/// <summary>The whole endpoint looks unreachable — skipped for every capability.</summary>
		Endpoint,

		/// <summary>The endpoint answered; only this capability's leg is throttled or erroring.</summary>
		Leg,
	}

	/// <param name="Scope">How wide the suppression is.</param>
	/// <param name="Remaining">How much longer it lasts.</param>
	/// <param name="FirstObservation">
	/// True exactly once per opening: the first caller to see this cooldown. Later callers see
	/// false, so the router can log the transition at Information and the repeats at Debug.
	/// </param>
	public readonly record struct Skip(SkipScope Scope, TimeSpan Remaining, bool FirstObservation);

	public bool IsOpen(string endpoint, LlmCapability capability) => OpenReason(endpoint, capability) is not null;

	/// <summary>
	/// Why this leg would be skipped and for how much longer, or <c>null</c> when it may be attempted.
	/// Reading this has the half-open side effect: once a cooldown has elapsed the state is cleared
	/// and the next call goes through.
	/// </summary>
	public Skip? OpenReason(string endpoint, LlmCapability capability)
	{
		if (Observe(_endpoints, endpoint, SkipScope.Endpoint) is { } ep) return ep;
		if (Observe(_legs, new LegKey(endpoint, capability), SkipScope.Leg) is { } leg) return leg;
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
		// Under the SAME lock Bump holds. Removing the entry without it lets a failure that already
		// holds a reference update a state nobody can reach any more — the failure is then silently
		// lost and the breaker under-counts.
		Clear(_endpoints, endpoint);
		Clear(_legs, new LegKey(endpoint, capability));
	}

	static void Clear<TKey>(ConcurrentDictionary<TKey, State> map, TKey key) where TKey : notnull
	{
		if (!map.TryGetValue(key, out var s)) return;
		lock (s)
		{
			map.TryRemove(key, out _);
			// A concurrent Bump may have re-added under the same key after our remove; if it did,
			// it did so on a state whose count starts at 0, which is the intended reset either way.
		}
	}

	void Bump<TKey>(ConcurrentDictionary<TKey, State> map, TKey key) where TKey : notnull
	{
		var s = map.GetOrAdd(key, _ => new State());
		lock (s)
		{
			s.ConsecutiveFailures++;
			if (s.ConsecutiveFailures >= FailureThreshold)
			{
				s.OpenUntil = _time.GetUtcNow() + OpenDuration;
				s.Announced = false; // a new opening is a new transition to report
			}
		}
	}

	// Returns null when this leg may be attempted, else the skip with its first-observation flag.
	//
	// NOTE ON WHAT THIS DOES NOT DO: once the cooldown elapses the state is NOT cleared here and NO
	// single probe is reserved — every caller arriving after expiry is let through at once, and the
	// first failure re-opens immediately because the consecutive-failure count is still at
	// threshold. That is a COOLDOWN, not a single-probe half-open, and it is the behaviour this
	// breaker had before failure classes existed; naming it accurately matters more than changing
	// it here, because a real half-open needs a decision about what a concurrent stampede costs.
	Skip? Observe<TKey>(ConcurrentDictionary<TKey, State> map, TKey key, SkipScope scope) where TKey : notnull
	{
		if (!map.TryGetValue(key, out var s) || s.OpenUntil is null) return null;
		var remaining = s.OpenUntil.Value - _time.GetUtcNow();
		if (remaining <= TimeSpan.Zero) return null;
		lock (s)
		{
			var first = !s.Announced;
			s.Announced = true;
			return new Skip(scope, remaining, first);
		}
	}
}
