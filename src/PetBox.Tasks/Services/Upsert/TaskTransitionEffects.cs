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

	// Terminal-blocker rule (work blocks-edge-closes-on-terminal-blocker), the symmetry
	// RunDeleteEffectsAsync just below has always had and entering-a-terminal-status never did: a
	// node that ENTERS a terminal status by its OWN board's FSM soft-closes every active OUTGOING
	// `blocks` edge. Before this, only a kind carrying the declared `On: Done, Link: blocks` effect
	// (i.e. `work`, and only on Done) ever released its dependents; on `simple`/`classic`/a
	// project-declared kind, `blockedBy` kept naming a finished blocker forever (observation
	// blocks-edge-never-closes-on-kind-without-blocksgate).
	//
	// TWO AXES, and THIS METHOD alone is universal on only ONE of them. Say both, because a reader
	// who takes "any kind" for "any way in" will trust it where it does not hold:
	//  - KIND: universal. Any kind, declared or preset, with or without an Effects entry, a
	//    BlocksGate, or a methodology document at all.
	//  - HOW THE NODE GOT THERE: this method itself only hangs off UpsertAsync (TasksService.cs,
	//    after RunTransitionEffectsAsync), so IT sees a status written by an upsert and nothing
	//    else. work terminal-blocks-release-not-called-from-cascade: a node driven into a terminal
	//    status by the CASCADE of a declared effect (SetActiveNodeStatusAsync — issue_task,
	//    SyncObservationOnObligationTerminalAsync, SetSupersedesAsync) used to write straight to
	//    TemporalStore and call nobody, leaving its own outgoing `blocks` edges active. That gap is
	//    now closed at the SOURCE: SetActiveNodeStatusAsync below calls
	//    CloseOutgoingBlocksEdgesOnTerminalEntryAsync itself on entering terminal, so every cascade
	//    door gets the same edge-closing behavior this method provides for the direct-upsert door.
	//    The backfill below still stays armed for fleet rows written before this fix shipped.
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
			await CloseOutgoingBlocksEdgesOnTerminalEntryAsync(projectKey, n.NodeId, n.Key, n.Board, n.Status, runtime, ct);
		}
	}

	// Shared by RunTerminalBlocksReleaseAsync (the UpsertAsync door) AND SetActiveNodeStatusAsync (the
	// CASCADE door — work terminal-blocks-release-not-called-from-cascade): a node ENTERING a terminal
	// status closes its own outgoing `blocks` edges and releases each dependent, whichever door drove
	// it there. Before this, only a node that entered its terminal status through UpsertAsync's OWN
	// patch list ever triggered this; a node driven there by a declared effect's cascade (issue_task,
	// SyncObservationOnObligationTerminalAsync) or by SetSupersedesAsync wrote straight to TemporalStore
	// and never called this, so its outgoing edges stayed active forever — the reason
	// TerminalBlockerEdgeBackfillMigrator's apply flag had to stay armed as a live compensation rather
	// than a one-time cleanup.
	async Task CloseOutgoingBlocksEdgesOnTerminalEntryAsync(
		string projectKey, string nodeId, string key, string board, string status,
		MethodologyRuntime runtime, CancellationToken ct)
	{
		foreach (var edge in (await _relations.ListAsync(projectKey, nodeId, "from", ct: ct))
			.Where(e => string.Equals(e.Kind, "blocks", StringComparison.OrdinalIgnoreCase)))
		{
			await _relations.CloseAsync(projectKey, "blocks", edge.FromNodeId, edge.ToNodeId, ct);
			// Relation has no "closed by" column — this line is the ONLY record of who closed
			// this edge and why, and therefore the only register a manual restore can read.
			_log?.LogInformation(
				"Tasks blocks-edge-closes-on-terminal-blocker: closed `blocks` edge {EdgeId} ({From} -> {To}) in project {Project} — blocker '{Key}' on board {Board} entered terminal status '{Status}'",
				edge.Id, edge.FromNodeId, edge.ToNodeId, projectKey, key, board, status);
			await ReleaseIfFullyUnblockedAsync(projectKey, edge.ToNodeId, runtime, ct);
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
		var (runtime, dependent) = await DependentContextAsync(projectKey, dependentNodeId, fallback, ct);

		// The status move is recorded HERE, from what actually happened, rather than predicted by
		// the caller. That is not a style preference — it is the rollback register's correctness.
		// The backfill migrator used to predict this from its own start-of-pass snapshot ("this
		// dependent has exactly one edge"), which is wrong the moment a dependent has TWO terminal
		// blockers: both closure lines would print `release=none` because the snapshot saw two
		// edges, while the SECOND close really does release the node. An operator replaying the
		// log would then restore the edges and silently leave the status moved. `relations` has no
		// "closed by" column, so the log is the only register there is; a register that omits a
		// write it performed is worse than no register, because it reads as complete.
		string? movedFrom = null;
		string? movedTo = null;
		await SetActiveNodeStatusAsync(projectKey, dependentNodeId, runtime,
			(_, node, _, targetKindSlug) =>
			{
				if (runtime.BlocksGate(targetKindSlug) is not { } gate) return null;
				if (!string.Equals(node.Status, gate.Status, StringComparison.OrdinalIgnoreCase)) return null;
				// SetActiveNodeStatusAsync no-ops when the target equals the current status, so
				// this guard keeps the record in step with the WRITE, not with the intent.
				if (string.Equals(gate.ReleaseTo, node.Status, StringComparison.OrdinalIgnoreCase)) return null;
				movedFrom = node.Status;
				movedTo = gate.ReleaseTo;
				return gate.ReleaseTo;
			}, ct);

		if (movedFrom is null) return;
		_log?.LogInformation(
			"Tasks blocks-edge-closes-on-terminal-blocker: RELEASED node={NodeId} ({Board}/{Key}) in project {Project} "
			+ "from={From} to={To} — its last active `blocks` edge is gone",
			dependentNodeId, dependent?.Board ?? "?", dependent?.Key ?? "?", projectKey, movedFrom, movedTo);
	}

	// The dependent's active row plus the methodology runtime of the board it ACTUALLY lives on —
	// which is not necessarily the board whose upsert/delete triggered the effect. The row is read
	// either way (it names the node in the release log); the runtime falls back to `fallback` when
	// no resolver is bound (direct construction outside TasksService) or there is no active row.
	async Task<(MethodologyRuntime Runtime, TaskNode? Node)> DependentContextAsync(
		string projectKey, string nodeId, MethodologyRuntime fallback, CancellationToken ct)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var node = ctx.TaskNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault();
		if (node is null || _boardRuntime is null) return (fallback, node);
		return (await _boardRuntime(projectKey, node.Board, ct), node);
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
		// declared-effects-use-source-board-runtime: resolve THIS node's OWN board runtime rather than
		// trusting `runtime` as passed — a caller driving a CASCADE (RunTransitionEffectsAsync's declared
		// effects, SetSupersedesAsync, SyncObservationOnObligationTerminalAsync) hands its OWN board's
		// runtime, which need not be the same methodology instance (or any instance) as the node actually
		// being written here. `_boardRuntime` resolves the TARGET board's own document, exactly like
		// DependentContextAsync above; `runtime` is only the fallback for an unbound resolver (direct
		// construction outside TasksService, e.g. tests).
		var targetRuntime = _boardRuntime is null ? runtime : await _boardRuntime(projectKey, node.Board, ct);
		var wf = targetRuntime.For(meta?.Kind, node.Type.Length == 0 ? null : node.Type);
		var wasTerminal = targetRuntime.IsTerminalStatus(meta?.Kind, node.Status);
		var target = pick(wf, node, wasTerminal, meta?.Kind);
		if (target is null || string.Equals(target, node.Status, StringComparison.OrdinalIgnoreCase)) return;
		// The CASCADE half of decision-pending-survives-closure: this door closes nodes too — the
		// work preset's `On: Done, Link: issue_task` effect drives the reported intake node to
		// `done` without ever passing through TasksService.ApplyWorkflow. Clearing the flag here,
		// in the same revision the status change mints, keeps the invariant "terminal ⇒ not
		// waiting" true whichever door did the closing. Terminality comes from the TARGET board's
		// FSM through the very predicate this method already asks about the CURRENT status one
		// line above — both terminal kinds, never a status spelling.
		var entersTerminal = targetRuntime.IsTerminalStatus(meta?.Kind, target);
		var pending = node.DecisionPending && !entersTerminal;
		await TemporalStore.UpsertAsync(ctx, new[] { node with { Status = target, DecisionPending = pending } }, partition: n => n.Board == node.Board, ct: ct);
		await _boards.TouchAsync(projectKey, node.Board, ct);
		// terminal-blocks-release-not-called-from-cascade: this method writes straight to TemporalStore,
		// bypassing UpsertAsync's own RunTerminalBlocksReleaseAsync (which only sees a node's OWN patch
		// list) — so a CASCADE into a terminal status must close its own outgoing `blocks` edges here,
		// the same way a direct upsert already does. Only on ENTERING terminal (wasTerminal false, entersTerminal
		// true), matching RunTerminalBlocksReleaseAsync's own guard — an already-terminal node touched again
		// must not re-close an edge a human deliberately reinstated.
		if (entersTerminal && !wasTerminal)
			await CloseOutgoingBlocksEdgesOnTerminalEntryAsync(projectKey, nodeId, node.Key, node.Board, target, targetRuntime, ct);
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
