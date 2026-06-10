namespace PetBox.Memory.Contract;

// Usage telemetry intake for memory entries (spec: memory-usage-observability).
// Called ONLY by the agent/human-facing adapters (MCP tools, UI) — never by internal
// machine consumers (distillation judge, digest discovery), which reach IMemoryService
// directly; that placement is what keeps the counters honest. Both calls are
// fire-and-forget enqueues: the read path never waits on a counter write, and a lost
// increment on crash costs statistics, not state.
public interface IMemoryUsageRecorder
{
	// The entry keys actually RETURNED in a recall/search answer (post-limit) — an impression.
	void Surfaced(string projectKey, string store, IReadOnlyList<string> keys);

	// A direct memory.get of one entry — an engagement (stronger than an impression).
	void Opened(string projectKey, string store, string key);

	// Drains everything enqueued so far to disk. For tests and graceful shutdown.
	Task FlushAsync(CancellationToken ct = default);
}

// One entry's usage as exposed on read surfaces (opt-in flags / UI).
public sealed record MemoryUsageView(long Surfaced, long Opened, DateTime? LastHitAt);
