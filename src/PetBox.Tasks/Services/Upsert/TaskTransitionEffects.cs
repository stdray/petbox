using PetBox.Core.Data.Temporal;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services.Upsert;

// Post-write FSM / delete effect stage for Tasks upsert. Owns transition-driven status
// cascades, delete cleanup (edges + tags + unblock), and the shared active-node status
// mutator used by effects and association supersedes.
public sealed class TaskTransitionEffects
{
	readonly ITaskBoardStore _boards;
	readonly IRelationStore _relations;
	readonly ITagStore _tags;

	public TaskTransitionEffects(ITaskBoardStore boards, IRelationStore relations, ITagStore tags)
	{
		_boards = boards;
		_relations = relations;
		_tags = tags;
	}

	// Data-driven FSM effects: when a node enters (default) or leaves (OnLeave, Effect.onLeave,
	// methodology-blocks-gate-data) a status, walk matching edges and apply Set / OnlyFrom (and
	// for `blocks`, consume the edge + release only the last blocker — that mechanism itself
	// stays builtin to the `blocks` link kind, not generalized to arbitrary link kinds).
	// `Set: null` declares a PURE edge-consumption effect: the edge is still closed (for
	// `blocks`), but no status is propagated to the linked node — the shape WorkKind's own
	// onLeave entry uses to replace the old CloseBlocksOnLeaveAsync (manually leaving the gate
	// status closes every incoming `blocks` edge, history kept, nobody's status forced).
	public async Task RunTransitionEffectsAsync(
		string projectKey, string? kindSlug, MethodologyRuntime runtime,
		PlanNode[] desired, Dictionary<string, PlanNode> prior, CancellationToken ct)
	{
		var effects = runtime.Effects(kindSlug);
		if (effects.Count == 0) return;
		foreach (var n in desired)
		{
			var cur = prior.GetValueOrDefault(n.Key) ?? (n.PrevKey is not null ? prior.GetValueOrDefault(n.PrevKey) : null);
			var statusChanged = cur is null || !string.Equals(cur.Status, n.Status, StringComparison.OrdinalIgnoreCase);
			if (!statusChanged || n.NodeId.Length == 0) continue;
			// onEnter effects trigger on the NEW status; onLeave effects trigger on the OLD one —
			// and need a prior row to have left at all (a brand-new node cannot "leave" anything).
			var matching = effects.Where(e => e.OnLeave
				? cur is not null && string.Equals(e.On, cur.Status, StringComparison.OrdinalIgnoreCase)
				: string.Equals(e.On, n.Status, StringComparison.OrdinalIgnoreCase));
			foreach (var e in matching)
			{
				var incoming = string.Equals(e.Direction, "incoming", StringComparison.OrdinalIgnoreCase);
				var edges = (await _relations.ListAsync(projectKey, n.NodeId, incoming ? "to" : "from", ct: ct))
					.Where(x => string.Equals(x.Kind, e.Link, StringComparison.OrdinalIgnoreCase)).ToList();
				foreach (var edge in edges)
				{
					var linkedId = incoming ? edge.FromNodeId : edge.ToNodeId;
					if (string.Equals(e.Link, "blocks", StringComparison.OrdinalIgnoreCase))
					{
						// gating semantics: consume the edge; release only the last blocker
						await _relations.CloseAsync(projectKey, "blocks", edge.FromNodeId, edge.ToNodeId, ct);
						if (e.Set is null) continue; // pure consume — no status to propagate, ever
						var stillBlocked = (await _relations.ListAsync(projectKey, linkedId, "to", ct: ct)).Any(x => x.Kind == "blocks");
						if (stillBlocked) continue;
					}
					if (e.Set is null) continue;
					await SetActiveNodeStatusAsync(projectKey, linkedId, runtime,
						(wf, node, isTerminal) =>
							isTerminal ? null
							: e.OnlyFrom is not null && !string.Equals(node.Status, e.OnlyFrom, StringComparison.OrdinalIgnoreCase) ? null
							: wf?.Status(e.Set)?.Slug, ct);
				}
			}
		}
	}

	// Delete effect: a temporal-closed node must not leave dangling structure behind — close
	// every edge touching it (both directions, any kind) and its tags. Unblocking mirrors the
	// Done effect: when the deleted node was a blocker, a target left with no blockers moves
	// Blocked → InProgress. System action (no gate).
	// NOTE (methodology-blocks-gate-data, scope boundary): this "Blocked"/"InProgress" literal
	// is intentionally NOT threaded through MethodologyRuntime.BlocksGate in this pass — unlike
	// RunTransitionEffectsAsync's blocks-effect above, this path runs UNGATED by kind (any board
	// whose preset happens to name a "Blocked" status gets this unblock-on-delete, including
	// non-gated kinds like `simple`); gating it on BlocksGate(kindSlug) would silently NARROW
	// that behavior with no test net proving today's cross-kind reach is even intentional. Filed
	// as a finding under work/umbrella-methodology-engine rather than changed here.
	public async Task RunDeleteEffectsAsync(
		string projectKey, string board, IReadOnlyList<NodePatch> deletePatches,
		Dictionary<string, PlanNode> prior, MethodologyRuntime runtime, CancellationToken ct)
	{
		foreach (var p in deletePatches)
		{
			if (!prior.TryGetValue(p.Key, out var row) || row.NodeId.Length == 0) continue;
			foreach (var e in await _relations.ListAsync(projectKey, row.NodeId, "both", ct: ct))
			{
				await _relations.DeleteAsync(projectKey, e.Id, ct);
				if (e.Kind == "blocks" && e.FromNodeId == row.NodeId)
				{
					var stillBlocked = (await _relations.ListAsync(projectKey, e.ToNodeId, "to", ct: ct)).Any(x => x.Kind == "blocks");
					if (!stillBlocked)
						await SetActiveNodeStatusAsync(projectKey, e.ToNodeId, runtime,
							(_, node, _) => string.Equals(node.Status, "Blocked", StringComparison.OrdinalIgnoreCase) ? "InProgress" : null, ct);
				}
			}
			// An empty list REPLACES the node's full tag set — i.e. soft-closes every active tag.
			await _tags.SetAsync(projectKey, board, row.NodeId, [], ct: ct);
		}
	}

	// Find the active node with this NodeId across the project's boards and move it to a
	// target status chosen by `pick` (null = leave as-is). System action (no gate). The
	// pick receives the target board's runtime-resolved workflow and whether the node's
	// CURRENT status is terminal for its board (per-kind classification).
	public async Task SetActiveNodeStatusAsync(
		string projectKey, string nodeId, MethodologyRuntime runtime,
		Func<PetBox.Tasks.Workflow.Workflow?, PlanNode, bool, string?> pick, CancellationToken ct)
	{
		// NodeId is unique across the project, so find the active row directly in the one
		// project file; its Board tells us which partition to write back into.
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var node = ctx.PlanNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault();
		if (node is null) return;
		var meta = await _boards.FindAsync(projectKey, node.Board, ct);
		var wf = runtime.For(meta?.Kind, node.Type.Length == 0 ? null : node.Type);
		var target = pick(wf, node, runtime.IsTerminalStatus(meta?.Kind, node.Status));
		if (target is null || string.Equals(target, node.Status, StringComparison.OrdinalIgnoreCase)) return;
		await TemporalStore.UpsertAsync(ctx, new[] { node with { Status = target } }, partition: n => n.Board == node.Board, ct: ct);
		await _boards.TouchAsync(projectKey, node.Board, ct);
	}
}
