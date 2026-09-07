using Microsoft.Extensions.Logging;
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
	// Optional: startup-log observability for the terminal-blocker edge closures this class
	// performs. There is NO "closed by" column on Relation (only ClosedAt), so the log line IS
	// the rollback register for an automatic closure — the same contract the backfill migrator
	// (TerminalBlockerEdgeBackfillMigrator) writes under. Null in the ~60 direct `new
	// TasksService(...)` test construction sites; every use is null-safe.
	readonly ILogger? _log;

	// Board-scoped methodology resolution (TasksService.GetRuntimeForBoardAsync), bound AFTER
	// construction because this object is built inside TasksService's own constructor — the same
	// circular-safe delegate pattern MethodologyTemplateService.BindInstanceRules uses. Null when
	// unbound: every consumer falls back to the runtime it was handed, i.e. exactly the
	// pre-binding behavior.
	Func<string, string, CancellationToken, Task<MethodologyRuntime>>? _boardRuntime;

	public TaskTransitionEffects(ITaskBoardStore boards, IRelationStore relations, ITagStore tags, ILogger? log = null)
	{
		_boards = boards;
		_relations = relations;
		_tags = tags;
		_log = log;
	}

	// Wire the per-board runtime resolver (see _boardRuntime). Called once, by TasksService.
	public void BindBoardRuntime(Func<string, string, CancellationToken, Task<MethodologyRuntime>> resolve) =>
		_boardRuntime = resolve;

	// Data-driven FSM effects: when a node enters (default) or leaves (OnLeave, Effect.onLeave,
	// methodology-blocks-gate-data) a status, walk matching edges and apply Set / OnlyFrom (and
	// for `blocks`, consume the edge + release only the last blocker — that mechanism itself
	// stays builtin to the `blocks` link kind, not generalized to arbitrary link kinds).
	// `Set: null` declares a PURE edge-consumption effect: the edge is still closed (for
	// `blocks`), but no status is propagated to the linked node. NOTE: no BUILTIN kind declares
	// an OnLeave effect today — TasksService.CloseBlocksOnLeaveAsync (the manual-leave-Blocked
	// unblock) stays its OWN imperative method rather than a WorkKind preset entry, because
	// MethodologyRuntime.Effects(kindSlug) resolves whole-object and a real quartet-provisioned
	// project's `work` kind already carries its own frozen, pre-existing Effects list (see the
	// comment on MethodologyTransitionEffectDef.OnLeave) — this generalization exists so a
	// PROJECT-DECLARED kind can opt into an onLeave effect, proven by schema/wire-round-trip
	// tests, not by any builtin preset routing through it.
	public async Task RunTransitionEffectsAsync(
		string projectKey, string? kindSlug, MethodologyRuntime runtime,
		TaskNode[] desired, Dictionary<string, TaskNode> prior, CancellationToken ct)
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
						(wf, node, isTerminal, _) =>
							isTerminal ? null
							: e.OnlyFrom is not null && !string.Equals(node.Status, e.OnlyFrom, StringComparison.OrdinalIgnoreCase) ? null
							: wf?.Status(e.Set)?.Slug, ct);
				}
			}
		}
	}

	// UNIVERSAL terminal-blocker rule (work blocks-edge-closes-on-terminal-blocker), the
	// symmetry RunDeleteEffectsAsync just below has always had and entering-a-terminal-status
	// never did: a node that ENTERS a terminal status by its OWN board's FSM soft-closes every
	// active OUTGOING `blocks` edge, on ANY kind — whether or not that kind declares an Effects
	// entry, a BlocksGate, or a methodology document at all. Before this, only a kind carrying
	// the declared `On: Done, Link: blocks` effect (i.e. `work`, and only on Done) ever released
	// its dependents; on `simple`/`classic`/a project-declared kind, `blockedBy` kept naming a
	// finished blocker forever (observation blocks-edge-never-closes-on-kind-without-blocksgate).
	//
	// Ordering: TasksService calls this AFTER RunTransitionEffectsAsync on purpose. A kind that
	// DOES declare a blocks effect gets to run its own — with its own Set/OnlyFrom semantics —
	// first; by the time we look, those edges are already closed and ListAsync (active-only) hands
	// us nothing, so a declared effect keeps priority and this never double-fires.
	//
	// THREE things this deliberately does:
	//  - Terminality via MethodologyRuntime.IsTerminalStatus (StatusKindOf's projection), never a
	//    string "Done". `runtime`/`kindSlug` here are the UPSERT board's — which IS the blocker's
	//    board, since every node in `desired` was written to it — so the blocker is classified by
	//    its own FSM.
	//  - ANY terminal, TerminalCancel included. A Cancelled blocker will never do anything again;
	//    leaving the edge open strands a gated dependent in `Blocked` with a blocker that cannot
	//    move, which GuardEngine.RequireBlockers then re-flags on every subsequent edit.
	//  - The DEPENDENT's release is judged on the DEPENDENT's board (useTargetBoardRuntime), not
	//    the blocker's — they need not share a board, a kind, or a methodology instance.
	// NOT done, deliberately (owner's call on the card): reopening the blocker does NOT restore
	// the edge. That is already how `work` behaves; the history stays readable via ClosedAt.
	public async Task RunTerminalBlocksReleaseAsync(
		string projectKey, string? kindSlug, MethodologyRuntime runtime,
		TaskNode[] desired, Dictionary<string, TaskNode> prior, CancellationToken ct)
	{
		foreach (var n in desired)
		{
			if (n.NodeId.Length == 0) continue;
			var cur = prior.GetValueOrDefault(n.Key) ?? (n.PrevKey is not null ? prior.GetValueOrDefault(n.PrevKey) : null);
			// ENTERS a terminal status: the new status is terminal AND it is not the status the
			// node already sat at. A node that was already terminal and stayed there is skipped —
			// otherwise every unrelated edit (a tag, a body) would re-scan its edges, and, worse,
			// an edge deliberately re-opened after the fact would be silently re-closed by the
			// next touch of an untouched node.
			if (!runtime.IsTerminalStatus(kindSlug, n.Status)) continue;
			if (cur is not null && string.Equals(cur.Status, n.Status, StringComparison.OrdinalIgnoreCase)) continue;
			foreach (var edge in (await _relations.ListAsync(projectKey, n.NodeId, "from", ct: ct))
				.Where(e => string.Equals(e.Kind, "blocks", StringComparison.OrdinalIgnoreCase)))
			{
				await _relations.CloseAsync(projectKey, "blocks", edge.FromNodeId, edge.ToNodeId, ct);
				// Relation has no "closed by" column — this line is the ONLY record of who closed
				// this edge and why, and therefore the only register a manual restore can read.
				_log?.LogInformation(
					"Tasks blocks-edge-closes-on-terminal-blocker: closed `blocks` edge {EdgeId} ({From} -> {To}) in project {Project} — blocker '{Key}' on board {Board} entered terminal status '{Status}'",
					edge.Id, edge.FromNodeId, edge.ToNodeId, projectKey, n.Key, n.Board, n.Status);
				await ReleaseIfFullyUnblockedAsync(projectKey, edge.ToNodeId, runtime, ct);
			}
		}
	}

	// The dependent half of both the terminal-blocker rule above and the delete path below: once
	// an edge is gone, a dependent left with NO remaining active `blocks` edges moves off its
	// kind's blocking-gate status to gate.ReleaseTo. Gated by MethodologyRuntime.BlocksGate of the
	// DEPENDENT's own kind, resolved through the DEPENDENT's board runtime — a kind that declares
	// no gate (`simple`, `classic`) is never touched, matching that it never gated the block.
	// Public so the one-time backfill (TerminalBlockerEdgeBackfillMigrator) releases dependents
	// through the EXACT code the live rule uses, rather than a second copy of the gate logic that
	// could drift from it.
	public async Task ReleaseIfFullyUnblockedAsync(string projectKey, string dependentNodeId, MethodologyRuntime fallback, CancellationToken ct)
	{
		var stillBlocked = (await _relations.ListAsync(projectKey, dependentNodeId, "to", ct: ct)).Any(x => x.Kind == "blocks");
		if (stillBlocked) return;
		// Resolve the dependent board's runtime BEFORE the call rather than inside
		// SetActiveNodeStatusAsync: the `pick` closure below reads BlocksGate off a runtime it
		// CAPTURES, so re-resolving deeper down would have fixed `wf`/`isTerminal` and left the
		// gate lookup still reading the blocker's document — the exact half-fix this rule exists
		// to avoid.
		var runtime = await RuntimeForNodeAsync(projectKey, dependentNodeId, fallback, ct);
		await SetActiveNodeStatusAsync(projectKey, dependentNodeId, runtime,
			(_, node, _, targetKindSlug) => runtime.BlocksGate(targetKindSlug) is { } gate
				&& string.Equals(node.Status, gate.Status, StringComparison.OrdinalIgnoreCase)
				? gate.ReleaseTo : null, ct);
	}

	// The methodology runtime of the board a node ACTUALLY lives on — which is not necessarily
	// the board whose upsert/delete triggered the effect. Falls back to `fallback` when no
	// resolver is bound (direct construction outside TasksService) or the node has no active row.
	async Task<MethodologyRuntime> RuntimeForNodeAsync(string projectKey, string nodeId, MethodologyRuntime fallback, CancellationToken ct)
	{
		if (_boardRuntime is null) return fallback;
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var board = ctx.TaskNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault()?.Board;
		return board is null ? fallback : await _boardRuntime(projectKey, board, ct);
	}

	// Delete effect: a temporal-closed node must not leave dangling structure behind — close
	// every edge touching it (both directions, any kind) and its tags. Unblocking mirrors the
	// Done effect: when the deleted node was a blocker, a target left with no blockers moves
	// off its kind's blocking-gate status to the gate's ReleaseTo. System action (no gate).
	// Gated by MethodologyRuntime.BlocksGate(targetKindSlug) — the SAME data the Done-effect
	// path (RunTransitionEffectsAsync) and GuardEngine.RequireBlockers read, no local literal.
	// A kind that does not declare a blocks gate (e.g. `simple`, a strict data preset) gets NO
	// auto-unblock on delete, matching that it never gated the block in the first place.
	public async Task RunDeleteEffectsAsync(
		string projectKey, string board, IReadOnlyList<NodePatch> deletePatches,
		Dictionary<string, TaskNode> prior, MethodologyRuntime runtime, CancellationToken ct)
	{
		foreach (var p in deletePatches)
		{
			if (!prior.TryGetValue(p.Key, out var row) || row.NodeId.Length == 0) continue;
			foreach (var e in await _relations.ListAsync(projectKey, row.NodeId, "both", ct: ct))
			{
				await _relations.DeleteAsync(projectKey, e.Id, ct);
				if (e.Kind == "blocks" && e.FromNodeId == row.NodeId)
					// Shared with the terminal-blocker rule above — and the reason that shared
					// helper exists: this call used to read BlocksGate off the DELETED node's
					// board runtime while passing the DEPENDENT's kind slug into it. On a
					// cross-board block (the dependent on another board, another kind, or another
					// methodology instance) that asked the wrong document whether the dependent's
					// kind is gated, so a legitimately gated dependent could silently not be
					// released. Now the dependent's OWN board answers.
					await ReleaseIfFullyUnblockedAsync(projectKey, e.ToNodeId, runtime, ct);
			}
			// An empty list REPLACES the node's full tag set — i.e. soft-closes every active tag.
			await _tags.SetAsync(projectKey, board, row.NodeId, [], ct: ct);
		}
	}

	// Find the active node with this NodeId across the project's boards and move it to a
	// target status chosen by `pick` (null = leave as-is). System action (no gate). The
	// pick receives the target board's runtime-resolved workflow, whether the node's
	// CURRENT status is terminal for its board (per-kind classification), and the target
	// board's kind slug (for gate lookups keyed by kind, e.g. RunDeleteEffectsAsync's
	// BlocksGate read below).
	public async Task SetActiveNodeStatusAsync(
		string projectKey, string nodeId, MethodologyRuntime runtime,
		Func<PetBox.Tasks.Workflow.Workflow?, TaskNode, bool, string?, string?> pick, CancellationToken ct)
	{
		// NodeId is unique across the project, so find the active row directly in the one
		// project file; its Board tells us which partition to write back into.
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var node = ctx.TaskNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault();
		if (node is null) return;
		var meta = await _boards.FindAsync(projectKey, node.Board, ct);
		var wf = runtime.For(meta?.Kind, node.Type.Length == 0 ? null : node.Type);
		var target = pick(wf, node, runtime.IsTerminalStatus(meta?.Kind, node.Status), meta?.Kind);
		if (target is null || string.Equals(target, node.Status, StringComparison.OrdinalIgnoreCase)) return;
		// The CASCADE half of decision-pending-survives-closure: this door closes nodes too — the
		// work preset's `On: Done, Link: issue_task` effect drives the reported intake node to
		// `done` without ever passing through TasksService.ApplyWorkflow. Clearing the flag here,
		// in the same revision the status change mints, keeps the invariant "terminal ⇒ not
		// waiting" true whichever door did the closing. Terminality comes from the TARGET board's
		// FSM through the very predicate this method already asks about the CURRENT status one
		// line above — both terminal kinds, never a status spelling.
		var pending = node.DecisionPending && !runtime.IsTerminalStatus(meta?.Kind, target);
		await TemporalStore.UpsertAsync(ctx, new[] { node with { Status = target, DecisionPending = pending } }, partition: n => n.Board == node.Board, ct: ct);
		await _boards.TouchAsync(projectKey, node.Board, ct);
	}

	// Stamp decisionPending:true on a node addressed by NodeId, without touching status — the
	// automatic "fixed, and it came back" alert (work observation-recurrence-after-fix-signal):
	// when a dedup hit lands on a `fixed` observation, the OBLIGATION that (supposedly) fixed it
	// gets flagged so the owner sees the regression in their own decision queue without a manual
	// sweep. System action, no gate — same posture as SetActiveNodeStatusAsync just above, minus
	// the auto-CLEAR that method applies on ENTERING a terminal status: here the target node is
	// very likely already terminal (that is exactly why it was recorded as FixedByNodeId), and
	// staying flagged despite that is the entire point of this call, not a stale leftover.
	public async Task SetDecisionPendingAsync(string projectKey, string nodeId, CancellationToken ct)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var node = ctx.TaskNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault();
		if (node is null || node.DecisionPending) return;
		await TemporalStore.UpsertAsync(ctx, new[] { node with { DecisionPending = true } }, partition: n => n.Board == node.Board, ct: ct);
		await _boards.TouchAsync(projectKey, node.Board, ct);
	}
}
