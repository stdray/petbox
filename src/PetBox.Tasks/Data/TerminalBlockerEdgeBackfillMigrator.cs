using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Services.Upsert;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Data;

// One-time, idempotent backfill for work blocks-edge-closes-on-terminal-blocker.
//
// The engine rule (TaskTransitionEffects.RunTerminalBlocksReleaseAsync) closes a `blocks` edge
// when its BLOCKER enters a terminal status. It only fires from now on, on an upsert — an edge
// whose blocker went terminal BEFORE the rule shipped stays active forever, and `blockedBy` keeps
// naming a finished blocker. This walks every project's stored edges once and closes exactly
// those.
//
// PRECONDITION IS ON THE EDGE, NOT ON A DOCUMENT — deliberately, and this is the difference from
// WorkDeferredStatusMigrator's "only an untouched copy of our preset" posture. There is nothing
// here that could be a project's own deliberate decision: an active `blocks` edge out of a node
// its OWN board's FSM classifies as terminal is a state the product has no reading for. So a
// project that customized its methodology is processed on the same terms as an untouched one.
//
// SAFETY POSTURE, because this writes to live project databases:
//  - DRY RUN BY DEFAULT (`apply: false`). A dry run opens the project files, reads, and writes
//    NOTHING — it only logs the plan.
//  - Every closure is logged BEFORE it happens, with the relation's primary key and both
//    endpoints. `Relation` has NO "closed by" column (only ClosedAt), so nothing in the data
//    distinguishes an edge this pass closed from one closed by a delete years ago — the LOG IS
//    THE ROLLBACK REGISTER, and it is written to be sufficient on its own (see RollbackHint).
//  - Idempotent: a closed edge is no longer active, so a second pass finds nothing. Re-running
//    after a partial failure is safe.
//  - Scoped to per-project tasks files. The Core DB is read (board discovery) and never written.
//
// Terminality is asked of the BLOCKER's own board (MethodologyRuntime.IsTerminalStatus via
// StatusKindOf) and the dependent's release of the DEPENDENT's own board — the two need not share
// a board, a kind, or a methodology instance. Both classifications go through
// TasksService.GetRuntimeForBoardAsync, the same resolver the live engine uses, rather than a
// reimplementation of instance/utility membership resolution here.
public sealed class TerminalBlockerEdgeBackfillMigrator
{
	const string BlocksKind = "blocks";

	// Printed once per pass, ahead of any closure line, so an operator reading the deploy log has
	// the undo procedure in the same place as the ids it applies to.
	// Deliberately avoids the literal `closing edge=` that marks a real CLOSURE line, so prose and
	// data stay greppable apart (an operator filtering the deploy log must not pick this up as one
	// of the edges it describes).
	const string RollbackHint =
		"Tasks terminal-blocker-edge-backfill: ROLLBACK for this pass = for every closure line below, take its "
		+ "relation id and run in tasks/<project>.db: UPDATE relations SET ClosedAt = NULL WHERE Id = '<relation id>'; "
		+ "and where the line reports a release of <from>-><to>, restore the dependent node's status to <from>. "
		+ "There is no `closed by` column on relations — these log lines are the only record of what this pass touched.";

	readonly ICoreDbFactory _dbf;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _boards;
	readonly RelationStore _relations;
	readonly TasksService _tasks;
	readonly TaskTransitionEffects _effects;
	readonly bool _apply;
	readonly ILogger? _log;

	// `apply: false` (the default) = DRY RUN: read and log the plan, write nothing.
	public TerminalBlockerEdgeBackfillMigrator(
		ICoreDbFactory dbf, IScopedDbFactory<TasksDb> factory, bool apply = false, ILogger? log = null)
	{
		_dbf = dbf;
		_factory = factory;
		_apply = apply;
		_log = log;
		_boards = new TaskBoardStore(dbf, factory);
		_relations = new RelationStore(factory);
		var tags = new TagStore(factory);
		_tasks = new TasksService(_boards, _relations, tags, new CommentService(factory));
		// The dependent-release half runs through the LIVE rule's own code, not a copy — see
		// TaskTransitionEffects.ReleaseIfFullyUnblockedAsync.
		_effects = new TaskTransitionEffects(_boards, _relations, tags, log);
		_effects.BindBoardRuntime(_tasks.GetRuntimeForBoardAsync);
	}

	// The full tally of the most recent Migrate() call — see StartupMigrationRun.
	public StartupMigrationRun.Result LastRun { get; private set; }

	// Number of edges closed (apply) or that WOULD be closed (dry run).
	public int Migrate()
	{
		using var db = _dbf.Open();
		var projects = StartupMigrationRun.DiscoverProjects(db, _factory.BaseDir);
		_log?.LogInformation(
			"Tasks terminal-blocker-edge-backfill: starting pass over {ProjectCount} project(s), mode={Mode}",
			projects.Count, _apply ? "APPLY (writes)" : "DRY-RUN (no writes)");
		_log?.LogInformation(RollbackHint);
		LastRun = StartupMigrationRun.Execute("terminal-blocker-edge-backfill", projects, MigrateProject, _log);
		// The shared aggregate line above speaks in "documents"; this pass closes EDGES, so it
		// says so in its own words rather than leaving an operator to translate.
		_log?.LogInformation(
			"Tasks terminal-blocker-edge-backfill: pass done — {Edges} `blocks` edge(s) {Verb} across {Projects}/{ProjectCount} project(s), {Failed} project(s) FAILED",
			LastRun.DocumentsTouched, _apply ? "CLOSED" : "would be closed (dry run — nothing written)",
			LastRun.ProjectsTouched, LastRun.ProjectCount, LastRun.ProjectsFailed);
		return LastRun.DocumentsTouched;
	}

	StartupMigrationRun.ProjectOutcome MigrateProject(string projectKey) =>
		new(MigrateProjectAsync(projectKey).GetAwaiter().GetResult(), Malformed: 0);

	async Task<int> MigrateProjectAsync(string projectKey)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		var edges = ctx.GetTable<Relation>()
			.Where(r => r.ClosedAt == null && r.Kind == BlocksKind)
			.ToList();
		if (edges.Count == 0) return 0;

		// One active revision per NodeId, for both endpoints of every edge.
		var endpoints = edges.SelectMany(e => new[] { e.FromNodeId, e.ToNodeId }).ToHashSet(StringComparer.Ordinal);
		var nodes = ctx.GetTable<TaskNode>()
			.Where(n => n.ActiveTo == null && endpoints.Contains(n.NodeId))
			.ToList()
			.GroupBy(n => n.NodeId, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

		var runtimes = new Dictionary<string, MethodologyRuntime>(StringComparer.OrdinalIgnoreCase);
		var closed = 0;
		foreach (var edge in edges)
		{
			// A dangling endpoint should be impossible (relations carry an ON DELETE CASCADE FK to
			// plan_node_ids), but this pass must never THROW on one and sink a whole project.
			if (!nodes.TryGetValue(edge.FromNodeId, out var blocker) || !nodes.TryGetValue(edge.ToNodeId, out var dependent))
			{
				_log?.LogWarning(
					"Tasks terminal-blocker-edge-backfill: project {Project}, edge {EdgeId} ({From} -> {To}) has an endpoint with no active node revision — skipped, needs a human",
					projectKey, edge.Id, edge.FromNodeId, edge.ToNodeId);
				continue;
			}

			var blockerRuntime = await RuntimeAsync(projectKey, blocker.Board, runtimes);
			var blockerKind = await KindAsync(projectKey, blocker.Board);
			if (!blockerRuntime.IsTerminalStatus(blockerKind, blocker.Status)) continue;

			// What the release WOULD do, computed for the log line (and only for it — the actual
			// release below runs through the live rule's own code). "Would release" needs both the
			// dependent's gate, resolved on the DEPENDENT's board, and this being its LAST active
			// blocker.
			var dependentKind = await KindAsync(projectKey, dependent.Board);
			var dependentRuntime = await RuntimeAsync(projectKey, dependent.Board, runtimes);
			var lastBlocker = edges.Count(e => e.ToNodeId == edge.ToNodeId) == 1;
			var release = lastBlocker
				&& dependentRuntime.BlocksGate(dependentKind) is { } gate
				&& string.Equals(dependent.Status, gate.Status, StringComparison.OrdinalIgnoreCase)
					? $"{dependent.Status}->{gate.ReleaseTo}"
					: "none";

			_log?.LogInformation(
				"Tasks terminal-blocker-edge-backfill: {Mode} closing edge={EdgeId} kind={Kind} from={From} to={To} "
				+ "project={Project} blocker={BlockerBoard}/{BlockerKey} blockerStatus={BlockerStatus} "
				+ "dependent={DependentBoard}/{DependentKey} dependentStatus={DependentStatus} release={Release}",
				_apply ? "APPLY" : "DRY-RUN", edge.Id, edge.Kind, edge.FromNodeId, edge.ToNodeId,
				projectKey, blocker.Board, blocker.Key, blocker.Status,
				dependent.Board, dependent.Key, dependent.Status, release);

			closed++;
			if (!_apply) continue;
			await _relations.CloseAsync(projectKey, BlocksKind, edge.FromNodeId, edge.ToNodeId, CancellationToken.None);
			await _effects.ReleaseIfFullyUnblockedAsync(projectKey, edge.ToNodeId, dependentRuntime, CancellationToken.None);
		}
		return closed;
	}

	async Task<MethodologyRuntime> RuntimeAsync(string projectKey, string board, Dictionary<string, MethodologyRuntime> cache)
	{
		if (cache.TryGetValue(board, out var hit)) return hit;
		return cache[board] = await _tasks.GetRuntimeForBoardAsync(projectKey, board, CancellationToken.None);
	}

	async Task<string?> KindAsync(string projectKey, string board) =>
		(await _boards.FindAsync(projectKey, board, CancellationToken.None))?.Kind;
}
