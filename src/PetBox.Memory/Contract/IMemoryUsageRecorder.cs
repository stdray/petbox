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
	// `deliberate` splits the honest value signal from noise: a DELIBERATE search (an agent/
	// human typing a query) counts toward DeliberateCount; a MACHINE pull (automatic hook
	// context priming, usage:"machine") bumps only SurfacedCount. Default deliberate — a bare
	// search is a human intent (spec: memoverhaul honest usage signal).
	void Surfaced(string projectKey, string store, IReadOnlyList<string> keys, bool deliberate = true);

	// A direct memory_get of one entry — an engagement (stronger than an impression).
	void Opened(string projectKey, string store, string key);

	// The rows a tool call actually DELIVERED, one event per entry (spec:
	// usage-cost-and-fit-separate). entry_usage answers "how often" with a counter; this
	// answers "at what CONTEXT COST, and how well did it FIT" — kept as raw components,
	// never collapsed into one scalar. Same fire-and-forget contract as the counters:
	// enqueued, drained in the background, dropped on overflow. `projectKey` is the
	// CONTAINER the entries came from (project or workspace) — the events land in its file.
	void Delivered(string projectKey, IReadOnlyList<MemoryDeliveryEvent> events);

	// Drains everything enqueued so far to disk. For tests and graceful shutdown.
	Task FlushAsync(CancellationToken ct = default);
}

// One entry as it was handed to a caller by one tool call (spec: usage-cost-and-fit-separate).
// COST and FIT stay separate, and both stay raw:
//   cost — DeliveredChars (body chars actually sent, after the bodyLen contract), BodyChars
//          (the entry's full body), RowChars (the row's whole serialized wire price).
//   fit  — Rank (1-based position in the answer; MMR reorders rows without touching the score),
//          ScoreRaw (the fused RRF score BEFORE recency decay) and KRel (that score over the
//          request's top-1 → a within-request [0,1] normalization; raw RRF has no meaningful
//          absolute scale, its ceiling is ~1/60).
// `Tool` is search | get | listing; a listing ran no relevance leg (ScoreRaw/KRel null), and a
// memory_get is a perfect fit by definition (KRel = 1, DeliveredChars = BodyChars).
public sealed record MemoryDeliveryEvent(
	string Tool,
	string Scope,
	string Store,
	string Key,
	int DeliveredChars,
	int BodyChars,
	int RowChars,
	int Rank,
	double? ScoreRaw,
	double? KRel,
	string? SessionId,
	string UsageSource);

// One entry's usage as exposed on read surfaces (opt-in flags / UI). `Deliberate` is the
// subset of `Surfaced` from deliberate (non-machine) searches — the honest value signal.
public sealed record MemoryUsageView(long Surfaced, long Opened, DateTime? LastHitAt, long Deliberate = 0);

// Store-wide usage aggregate (spec: memory-usage-aggregate) — a single glance at how a
// store's entries are actually reached. Coverage (how many entries ever surfaced/opened
// and the fractions over the active set), the recency of the surfaced set, and the dead
// tail (entries that never once surfaced — the prime pruning candidates). Pure telemetry,
// derived from the same entry_usage counters GetUsageAsync reads; never load-bearing.
public sealed record MemoryUsageAggregate(
	int TotalEntries,
	int SurfacedAtLeastOnce,
	// The honest cut of SurfacedAtLeastOnce: entries reached by at least one DELIBERATE
	// search (not just automatic machine pulls) — the coverage that actually proved value.
	int DeliberatelySurfacedAtLeastOnce,
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
