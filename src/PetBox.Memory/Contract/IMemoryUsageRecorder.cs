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

	// A direct memory_get of one entry — an engagement (stronger than an impression).
	void Opened(string projectKey, string store, string key);

	// Drains everything enqueued so far to disk. For tests and graceful shutdown.
	Task FlushAsync(CancellationToken ct = default);
}

// One entry's usage as exposed on read surfaces (opt-in flags / UI).
public sealed record MemoryUsageView(long Surfaced, long Opened, DateTime? LastHitAt);

// Store-wide usage aggregate (spec: memory-usage-aggregate) — a single glance at how a
// store's entries are actually reached. Coverage (how many entries ever surfaced/opened
// and the fractions over the active set), the recency of the surfaced set, and the dead
// tail (entries that never once surfaced — the prime pruning candidates). Pure telemetry,
// derived from the same entry_usage counters GetUsageAsync reads; never load-bearing.
public sealed record MemoryUsageAggregate(
	int TotalEntries,
	int SurfacedAtLeastOnce,
	int OpenedAtLeastOnce,
	// Fractions over the ACTIVE entry set (0 when the store is empty).
	double SurfacedFraction,
	double OpenedFraction,
	// Median LastHitAt among entries that surfaced at least once (null = none surfaced);
	// "давность" = now − this. A real observed median TIMESTAMP (not an age) keeps it
	// deterministic — the caller/UI turns it into an age against the current clock.
	DateTime? MedianLastHitAt,
	MemoryDeadTail DeadTail);

// The never-surfaced tail: the total count plus an oldest-first sample of their keys (by
// entry Created — the most stale entries first, the best pruning candidates), capped at N.
public sealed record MemoryDeadTail(int Count, IReadOnlyList<string> TopKeys);
