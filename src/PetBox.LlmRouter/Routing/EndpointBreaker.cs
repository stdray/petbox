using System.Collections.Concurrent;

namespace PetBox.LlmRouter.Routing;

// A tiny per-endpoint circuit breaker (llm-fast-down). After `FailureThreshold` consecutive
// failures an endpoint is "open" for `OpenDuration` — the router skips it without attempting
// a connection, so a dead endpoint (e.g. the home PC asleep) never costs a connect-timeout on
// every call. A success resets it. Singleton + thread-safe; clock via TimeProvider for tests.
public sealed class EndpointBreaker
{
	public int FailureThreshold { get; init; } = 2;
	public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);

	readonly TimeProvider _time;
	readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

	public EndpointBreaker(TimeProvider time) => _time = time;

	sealed class State
	{
		public int ConsecutiveFailures;
		public DateTimeOffset? OpenUntil;
	}

	public bool IsOpen(string endpoint)
	{
		if (!_states.TryGetValue(endpoint, out var s) || s.OpenUntil is null) return false;
		if (_time.GetUtcNow() < s.OpenUntil) return true;
		// Cooldown elapsed — half-open: let the next attempt through.
		s.OpenUntil = null;
		return false;
	}

	public void RecordSuccess(string endpoint) => _states.TryRemove(endpoint, out _);

	public void RecordFailure(string endpoint)
	{
		var s = _states.GetOrAdd(endpoint, _ => new State());
		lock (s)
		{
			s.ConsecutiveFailures++;
			if (s.ConsecutiveFailures >= FailureThreshold)
				s.OpenUntil = _time.GetUtcNow() + OpenDuration;
		}
	}
}
