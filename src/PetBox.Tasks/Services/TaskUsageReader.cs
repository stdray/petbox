using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services;

// The read side of node_usage + node_delivery_events (spec: task-usage-layer-with-declared-role).
// Every aggregate it returns carries the board's DECLARED ROLE, because that is what makes the
// numbers mean anything: `surfaced 40 / opened 0` on a corpus board is waste, and the identical
// row on an index board is the design working. Reading one as the other is not hypothetical —
// memory's `session-digests` store was read as the worst in the system on exactly that shape.
public sealed class TaskUsageReader : ITaskUsageReader
{
	readonly ITaskBoardStore _boards;

	public TaskUsageReader(ITaskBoardStore boards) => _boards = boards;

	// The window the cost/fit legs judge on unless the caller says otherwise: recent behaviour,
	// not the board's whole life.
	public static readonly TimeSpan DefaultUsageWindow = TimeSpan.FromDays(30);

	public async Task<IReadOnlyDictionary<string, NodeUsageView>> GetUsageAsync(string projectKey, string board,
		IReadOnlyCollection<string>? nodeIds = null, CancellationToken ct = default)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var q = ctx.NodeUsage.Where(u => u.Board == board);
		if (nodeIds is not null) q = q.Where(u => nodeIds.Contains(u.NodeId));
		// The counters answer "how often"; delivery events answer "at what cost, how well" —
		// all-time here (a per-row read surface reports the node's whole life; the board
		// aggregate asks the WINDOWED sibling).
		var cost = DeliveryRollup(ctx, board, window: null, nodeIds);
		var view = new Dictionary<string, NodeUsageView>(StringComparer.Ordinal);
		foreach (var u in await q.ToListAsync(ct))
		{
			var d = cost.GetValueOrDefault(u.NodeId);
			view[u.NodeId] = new NodeUsageView(u.SurfacedCount, u.OpenedCount, u.LastHitAt, u.DeliberateCount,
				d?.Deliveries ?? 0, d?.DeliveredChars ?? 0, d?.AvgKRel);
		}

		return view;
	}

	// One node's delivery roll-up plus the RAW fit parts. AvgKRel must stay decomposed here: a
	// board-level mean is Σscore/Σn over EVENTS — averaging per-node means would weight a
	// once-delivered node the same as a hundred-times-delivered one.
	sealed record Rollup(long Deliveries, long DeliveredChars, long RowChars, double KRelSum, long KRelCount,
		long DeliberateDeliveries, long DeliberateDeliveredChars, double DeliberateKRelSum, long DeliberateKRelCount,
		long MachineDeliveries, long MachineDeliveredChars, double MachineKRelSum, long MachineKRelCount)
	{
		public double? AvgKRel => KRelCount == 0 ? null : KRelSum / KRelCount;
		public double? DeliberateAvgKRel => DeliberateKRelCount == 0 ? null : DeliberateKRelSum / DeliberateKRelCount;
		public double? MachineAvgKRel => MachineKRelCount == 0 ? null : MachineKRelSum / MachineKRelCount;
	}

	// node_delivery_events → per-node cost/fit, grouped in SQL (the table is append-only and
	// grows with every delivered row — it is never pulled into memory). `window` null = all time.
	static Dictionary<string, Rollup> DeliveryRollup(TasksDb ctx, string board, TimeSpan? window,
		IReadOnlyCollection<string>? nodeIds)
	{
		var q = ctx.NodeDeliveries.Where(d => d.Board == board);
		if (window is { } w)
		{
			var since = DateTime.UtcNow - w;
			q = q.Where(d => d.Ts >= since);
		}

		if (nodeIds is not null) q = q.Where(d => nodeIds.Contains(d.NodeId));
		const string deliberate = NodeUsageSourceKind.Deliberate;
		const string machine = NodeUsageSourceKind.Machine;
		return q.GroupBy(d => d.NodeId)
			.Select(g => new
			{
				NodeId = g.Key,
				Deliveries = g.LongCount(),
				DeliveredChars = g.Sum(d => d.DeliveredChars),
				RowChars = g.Sum(d => d.RowChars),
				// A listing carries no KRel (no relevance leg ran) — those events contribute cost
				// but no fit, so the fit denominator counts only the events that HAVE one.
				KRelSum = g.Sum(d => d.KRel ?? 0d),
				KRelCount = g.Sum(d => d.KRel != null ? 1L : 0L),
				// Same shape, filtered to one UsageSource — a CASE WHEN inside the same grouped
				// query, not a second round trip.
				DeliberateDeliveries = g.Sum(d => d.UsageSource == deliberate ? 1L : 0L),
				DeliberateDeliveredChars = g.Sum(d => d.UsageSource == deliberate ? d.DeliveredChars : 0),
				DeliberateKRelSum = g.Sum(d => d.UsageSource == deliberate ? (d.KRel ?? 0d) : 0d),
				DeliberateKRelCount = g.Sum(d => d.UsageSource == deliberate && d.KRel != null ? 1L : 0L),
				MachineDeliveries = g.Sum(d => d.UsageSource == machine ? 1L : 0L),
				MachineDeliveredChars = g.Sum(d => d.UsageSource == machine ? d.DeliveredChars : 0),
				MachineKRelSum = g.Sum(d => d.UsageSource == machine ? (d.KRel ?? 0d) : 0d),
				MachineKRelCount = g.Sum(d => d.UsageSource == machine && d.KRel != null ? 1L : 0L),
			})
			.ToList()
			.ToDictionary(r => r.NodeId,
				r => new Rollup(r.Deliveries, r.DeliveredChars, r.RowChars, r.KRelSum, r.KRelCount,
					r.DeliberateDeliveries, r.DeliberateDeliveredChars, r.DeliberateKRelSum, r.DeliberateKRelCount,
					r.MachineDeliveries, r.MachineDeliveredChars, r.MachineKRelSum, r.MachineKRelCount),
				StringComparer.Ordinal);
	}

	public async Task<BoardUsageAggregate> GetBoardUsageAsync(string projectKey, string board,
		int deadTailLimit = 10, TimeSpan? window = null, CancellationToken ct = default)
	{
		// The declared role, resolved ONCE and carried into the result. A board missing from the
		// catalog reads `corpus` — the conservative default, never null and never a throw.
		var meta = await _boards.FindAsync(projectKey, board, ct);
		var role = BoardDeclaredRole.Normalize(meta?.DeclaredRole);

		using var ctx = _boards.NewEnsuredConnection(projectKey);
		// Active nodes are the denominator (usage rows can outlive a deleted node, so we join
		// FROM the active set, not the counter table).
		var nodes = await ctx.TaskNodes
			.Where(n => n.Board == board && n.ActiveTo == null)
			.Select(n => new { n.NodeId, n.Key, n.Created })
			.ToListAsync(ct);
		var usage = (await ctx.NodeUsage.Where(u => u.Board == board).ToListAsync(ct))
			.ToDictionary(u => u.NodeId, StringComparer.Ordinal);

		var surfacedCount = 0;
		var deliberateCount = 0;
		var openedCount = 0;
		var surfacedHits = new List<DateTime>();
		var dead = new List<(string Key, DateTime Created)>();
		foreach (var n in nodes)
		{
			usage.TryGetValue(n.NodeId, out var u);
			if (u is { SurfacedCount: > 0 })
			{
				surfacedCount++;
				if (u.LastHitAt is { } hit) surfacedHits.Add(hit);
			}
			else
			{
				dead.Add((n.Key, n.Created)); // never surfaced — a dead-tail candidate
			}

			if (u is { DeliberateCount: > 0 }) deliberateCount++; // honest value cut
			if (u is { OpenedCount: > 0 }) openedCount++;
		}

		var total = nodes.Count;
		var deadTail = new BoardDeadTail(
			dead.Count,
			dead.OrderBy(d => d.Created).ThenBy(d => d.Key, StringComparer.Ordinal)
				.Take(Math.Max(0, deadTailLimit)).Select(d => d.Key).ToList());

		// The second dimension: what this board SPENT of the caller's context in the window, and
		// how well what it spent it on actually fitted. The board-level fit is
		// Σ(kRel)/Σ(events with a kRel) — event-weighted, so a row delivered a hundred times
		// weighs a hundred times.
		var win = window ?? DefaultUsageWindow;
		var rollup = DeliveryRollup(ctx, board, win, nodeIds: null);
		var active = nodes.Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);
		var kRelSum = 0d;
		var kRelCount = 0L;
		long deliveries = 0, deliveredChars = 0, rowChars = 0;
		var nodesDelivered = 0;
		long delibDeliveries = 0, delibChars = 0;
		var delibKRelSum = 0d;
		var delibKRelCount = 0L;
		long machDeliveries = 0, machChars = 0;
		var machKRelSum = 0d;
		var machKRelCount = 0L;
		foreach (var (nodeId, r) in rollup)
		{
			// Cost counts every row the board SENT — including one whose node has since been
			// deleted: the context it burned was real. NodesDelivered stays on the active set (it
			// is a coverage number, and its denominator is TotalNodes).
			deliveries += r.Deliveries;
			deliveredChars += r.DeliveredChars;
			rowChars += r.RowChars;
			kRelSum += r.KRelSum;
			kRelCount += r.KRelCount;
			delibDeliveries += r.DeliberateDeliveries;
			delibChars += r.DeliberateDeliveredChars;
			delibKRelSum += r.DeliberateKRelSum;
			delibKRelCount += r.DeliberateKRelCount;
			machDeliveries += r.MachineDeliveries;
			machChars += r.MachineDeliveredChars;
			machKRelSum += r.MachineKRelSum;
			machKRelCount += r.MachineKRelCount;
			if (active.Contains(nodeId)) nodesDelivered++;
		}

		return new BoardUsageAggregate(
			Board: board,
			DeclaredRole: role,
			TotalNodes: total,
			SurfacedAtLeastOnce: surfacedCount,
			DeliberatelySurfacedAtLeastOnce: deliberateCount,
			OpenedAtLeastOnce: openedCount,
			SurfacedFraction: total == 0 ? 0 : (double)surfacedCount / total,
			OpenedFraction: total == 0 ? 0 : (double)openedCount / total,
			MedianLastHitAt: Median(surfacedHits),
			DeadTail: deadTail,
			Cost: new BoardUsageCost(
				WindowDays: (int)Math.Round(win.TotalDays),
				Deliveries: deliveries,
				DeliveredChars: deliveredChars,
				RowChars: rowChars,
				AvgKRel: kRelCount == 0 ? null : kRelSum / kRelCount,
				NodesDelivered: nodesDelivered,
				DeliberateDeliveries: delibDeliveries,
				DeliberateDeliveredChars: delibChars,
				DeliberateAvgKRel: delibKRelCount == 0 ? null : delibKRelSum / delibKRelCount,
				MachineDeliveries: machDeliveries,
				MachineDeliveredChars: machChars,
				MachineAvgKRel: machKRelCount == 0 ? null : machKRelSum / machKRelCount));
	}

	// A real observed median TIMESTAMP (not an age) keeps it deterministic — the caller turns it
	// into an age against the current clock. Even counts take the lower middle, so the value is
	// always one that was actually observed rather than an interpolation of two.
	static DateTime? Median(List<DateTime> values)
	{
		if (values.Count == 0) return null;
		values.Sort();
		return values[(values.Count - 1) / 2];
	}
}
