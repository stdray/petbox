namespace PetBox.Tasks.Contract;

// The two UsageSource wire values for TASK-node delivery (spec:
// task-usage-layer-with-declared-role) — the ONE place these literal strings are spelled out on
// the Tasks side. Both the write side (TasksTools' `usageSource` argument on tasks_search /
// tasks_node_get, which validates against exactly these two) and the read side
// (TaskUsageReader's cost/fit split) match against these constants.
//
// This deliberately DUPLICATES PetBox.Memory.Contract.UsageSourceKind's two values rather than
// sharing a type: PetBox.Tasks does not reference PetBox.Memory (module independence is enforced
// by the boundary tests), so the choice is a duplicated PAIR OF STRINGS or a new cross-module
// dependency. The strings are a wire vocabulary shared with memory ON PURPOSE — a report that
// puts task and memory telemetry side by side must be able to group by the same two words — and
// they are pinned in both directions by a test (UsageSourceVocabularyMatchesMemory).
public static class NodeUsageSourceKind
{
	public const string Deliberate = "deliberate";
	public const string Machine = "machine";

	// Strict parse of a caller-supplied value: true + the canonical form, false for anything
	// else (blank included — an omitted argument is the caller's default to pick, not ours).
	// Never silently folds an unknown value into "deliberate": that would inflate the honest
	// signal with traffic nobody vouched for, which is the one thing this split exists to stop.
	public static bool TryNormalize(string? source, out string normalized)
	{
		var trimmed = source?.Trim();
		if (string.Equals(trimmed, Deliberate, StringComparison.OrdinalIgnoreCase)) { normalized = Deliberate; return true; }
		if (string.Equals(trimmed, Machine, StringComparison.OrdinalIgnoreCase)) { normalized = Machine; return true; }
		normalized = Deliberate;
		return false;
	}
}

// Usage telemetry intake for TASK NODES — the delivery-side twin of IMemoryUsageRecorder
// (spec: task-usage-layer-with-declared-role).
//
// Called ONLY by the adapters that hand a node to a caller (the MCP tools). Every call is a
// fire-and-forget enqueue onto a bounded channel: the read path never waits on a counter write,
// and a lost increment costs statistics, not state. Unlike memory's recorder, an overflow drop
// here is COUNTED and logged (see DroppedEvents) — silent loss makes the numbers unfalsifiable,
// and a telemetry surface nobody can falsify is worse than none.
//
// TWO AXES, BOTH FROM THE FIRST DAY (owner decision 2026-08-27): the counters answer how often a
// node was SURFACED vs OPENED (the price of showing it), the delivery events answer at what
// CONTEXT COST and how well it FIT. `deliberate` vs `machine` is the third, orthogonal cut and
// ships in the same migration — memory added it two months late (M008 after M007) and every
// number in between mixed automated context priming with intent, which made the whole signal
// unusable for the one decision it existed to support.
public interface ITaskUsageRecorder
{
	// The nodes actually RETURNED in a search/listing answer (post-limit, post-budget) — an
	// impression. `deliberate` splits honest value from noise: an agent/human typing a query
	// counts toward DeliberateCount; an automatic machine pull bumps only SurfacedCount.
	void Surfaced(string projectKey, string board, IReadOnlyList<string> nodeIds, bool deliberate = true);

	// A direct tasks_node_get of one node — an engagement, and the task-side mirror of
	// memory_get. A CLICK in the UI is deliberately NOT counted (owner decision): the UI opens
	// nodes for reasons that have nothing to do with whether an agent's context needed them.
	void Opened(string projectKey, string board, string nodeId);

	// The rows a tool call actually DELIVERED, one event per node. Kept as raw components,
	// never collapsed into a single "value" scalar. Same fire-and-forget contract as the
	// counters. `projectKey` names the tasks file the events land in.
	void Delivered(string projectKey, IReadOnlyList<TaskDeliveryEvent> events);

	// Events discarded because the bounded channel was full, since process start. This is the
	// honesty knob: telemetry that drops under load must SAY it dropped, or a low counter is
	// indistinguishable from a quiet system and every conclusion drawn from it is unsound.
	long DroppedEvents { get; }

	// Drains everything enqueued so far to disk. For tests and graceful shutdown.
	Task FlushAsync(CancellationToken ct = default);
}

// One node as it was handed to a caller by one tool call. COST and FIT stay separate and raw:
//   cost — DeliveredChars (body chars actually sent, after the bodyLen contract), BodyChars
//          (the node's full body), RowChars (the row's whole serialized wire price).
//   fit  — Rank (1-based position in the answer), ScoreRaw (the fused relevance score) and KRel
//          (that score over the request's top-1 → a within-request [0,1] normalization; a raw
//          rank-based fused score has no meaningful absolute scale).
// `Tool` is search | get | listing; a listing ran no relevance leg (ScoreRaw/KRel null), and a
// tasks_node_get is a perfect fit by definition (KRel = 1, DeliveredChars = the body sent).
//
// The owning board's DECLARED ROLE is stamped into the STORED row (owner decision 2026-08-27): a
// cost/fit number that can be read WITHOUT its role is a number that WILL be read without its
// role, and then an index gets judged by corpus expectations — the `session-digests` mistake,
// reproduced. It is deliberately NOT a field of this intake record: the RECORDER resolves it from
// the board catalog at write time, so no caller can supply a role and no caller can forget to.
// A later re-declaration re-frames future deliveries without rewriting past ones.
public sealed record TaskDeliveryEvent(
	string Tool,
	string Board,
	string NodeId,
	string Key,
	int DeliveredChars,
	int BodyChars,
	int RowChars,
	int Rank,
	double? ScoreRaw,
	double? KRel,
	string? SessionId,
	string UsageSource);

// One node's usage as exposed on a read surface (opt-in flags / UI). `Deliberate` is the subset
// of `Surfaced` from deliberate (non-machine) reads. The counters answer HOW OFTEN; the
// delivery-derived pair answers what the node COST (DeliveredChars) and how well it FIT
// (AvgKRel — null when no delivery carried one, i.e. listings only).
public sealed record NodeUsageView(
	long Surfaced, long Opened, DateTime? LastHitAt, long Deliberate = 0,
	long Deliveries = 0, long DeliveredChars = 0, double? AvgKRel = null);

// A whole board's usage, read against its DECLARED ROLE (spec:
// task-usage-layer-with-declared-role). `DeclaredRole` leads the record because it is the thing
// that makes the rest interpretable: on a `corpus` board a large DeadTail is waste, on an
// `index` board the same number is coverage. A reader that ignores it repeats the
// `session-digests` mis-read, which is why the role travels WITH the numbers rather than being
// something the caller is trusted to look up separately.
public sealed record BoardUsageAggregate(
	string Board,
	string DeclaredRole,
	int TotalNodes,
	int SurfacedAtLeastOnce,
	// The honest cut: nodes reached by at least one DELIBERATE read, not just machine pulls.
	int DeliberatelySurfacedAtLeastOnce,
	int OpenedAtLeastOnce,
	// Fractions over the ACTIVE node set (0 when the board is empty).
	double SurfacedFraction,
	double OpenedFraction,
	// Median LastHitAt among nodes that surfaced at least once (null = none surfaced).
	DateTime? MedianLastHitAt,
	BoardDeadTail DeadTail,
	BoardUsageCost Cost);

// What a board COST and how well it FIT over the aggregate's window. Cost is chars, not a rate.
// Read the two TOGETHER, and read both against DeclaredRole: high chars + low fit on a corpus
// board is a noise boar; the same shape on an index board may be exactly the job.
public sealed record BoardUsageCost(
	int WindowDays,
	long Deliveries,
	long DeliveredChars,
	long RowChars,
	double? AvgKRel,
	// Distinct active nodes delivered at least once in the window.
	int NodesDelivered,
	// The same cost/fit split by NodeUsageSourceKind — additive, never a replacement: filtering
	// down to deliberate-only would make machine cost invisible and a board serviced mostly by
	// automation would read as dead.
	long DeliberateDeliveries = 0, long DeliberateDeliveredChars = 0, double? DeliberateAvgKRel = null,
	long MachineDeliveries = 0, long MachineDeliveredChars = 0, double? MachineAvgKRel = null);

// The never-surfaced tail: the count plus an oldest-first sample of node slugs.
public sealed record BoardDeadTail(int Count, IReadOnlyList<string> TopKeys);

// The read side of the task usage tables (node_usage + node_delivery_events). Separate from
// ITasksService on purpose: usage is telemetry ABOUT the board, never board state, and keeping
// it behind its own door means no read path can accidentally make a counter load-bearing.
public interface ITaskUsageReader
{
	// Per-node usage for a board, optionally narrowed to a set of NodeIds. The key of the
	// returned map is the NODE ID (stable across a slug rename — the counters must survive one,
	// or a re-key silently resets a node's whole measured history).
	Task<IReadOnlyDictionary<string, NodeUsageView>> GetUsageAsync(string projectKey, string board,
		IReadOnlyCollection<string>? nodeIds = null, CancellationToken ct = default);

	// The board-wide aggregate, carrying the board's declared role (default 30d window).
	Task<BoardUsageAggregate> GetBoardUsageAsync(string projectKey, string board,
		int deadTailLimit = 10, TimeSpan? window = null, CancellationToken ct = default);
}
