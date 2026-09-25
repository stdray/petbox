namespace PetBox.Web.Search;

// Process-lifetime heartbeat throttle for a vectorization job's "I'm alive" log line.
// SearchEnrichmentService opens a FRESH DI scope every 60s tick, so a new
// TasksVectorizationJob/MemoryVectorizationJob is constructed each pass — an instance field on the
// job cannot survive across ticks. Registered as a DI singleton (Program.cs) instead, so the throttle
// state is a real collaborator the job receives rather than static state hidden inside it: tests get
// isolation for free by constructing a fresh clock, with no test-only reset seam required.
public class HeartbeatThrottle
{
	readonly Lock _lock = new();
	DateTimeOffset? _last;

	// Atomic check-and-set: true exactly when `interval` has elapsed since the last successful fire
	// (or this is the first call ever), and immediately records `now` as the new last-fire time.
	public bool TryFire(TimeSpan interval, DateTimeOffset now)
	{
		lock (_lock)
		{
			if (_last is { } last && now - last < interval) return false;
			_last = now;
			return true;
		}
	}
}

// Distinct type (not a shared HeartbeatThrottle singleton) so TasksVectorizationJob's hourly cadence
// cannot suppress or be suppressed by MemoryVectorizationJob's.
public sealed class TasksVectorizationHeartbeatClock : HeartbeatThrottle;

public sealed class MemoryVectorizationHeartbeatClock : HeartbeatThrottle;
