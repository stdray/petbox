using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Services;
using PetBox.Tasks.Services.Methodology;

namespace PetBox.Tasks.Data;

// One-time, idempotent (legacy-node-empty-nodeid-404): repairs existing rows across every
// project's tasks file that carry an empty NodeId, an empty Type, and/or a Status outside
// their board kind's FSM. No live write path can produce this any more — TasksService.
// ApplyWorkflow (the normal tasks_upsert path) always assigns a fresh NodeId and a
// kind-resolved default type/status, and the two paths that write straight to TemporalStore
// and skip ApplyWorkflow (QuickAddAsync, ReportIssueAsync) were already hardened to assign
// NodeId explicitly (see their own comments) — but nothing repairs a row already stuck in
// this state (a legacy/import-era row, or one written before those two fixes landed), and an
// empty NodeId is fatal to addressability forever once it exists: NodeRefResolver.
// FindActiveBySlug drops any row with NodeId.Length == 0 by construction, so such a row is
// still listed by tasks_search (a raw active-row scan has no such guard) but 404s from
// tasks_node_get and the UI detail page (both resolve through NodeRefResolver) with no way
// out.
//
// Two-phase repair, and the split is load-bearing, not stylistic:
//
//   1. NodeId is deliberately NOT a TaskNode payload field (TaskNode.SamePayload excludes it
//      — "assigned at birth ... NOT part of SamePayload"), so an ordinary
//      TemporalStore.UpsertAsync of a node whose Type/Status are ALREADY valid and only
//      NodeId needs fixing would classify as a no-op (TemporalStore.Classify's
//      `current.SamePayload(d)` branch fires before NodeId is ever looked at) and silently
//      write nothing. That is exactly why M004_StatusSlugAndNodeId's own NodeId backfill
//      used a raw SQL UPDATE instead of the ordinary upsert path — this migrator does the
//      same, patching the column in place (same revision: identity plumbing, not a content
//      edit), before phase 2 ever reads the row.
//   2. Type/Status ARE real payload fields, and defaulting them is a genuine semantic edit
//      that deserves its own revision (mirrors WorkDeferredStatusMigrator's Deferred ->
//      Cancelled move): this goes through MethodologyLiveMigration.RewriteAsync — the same
//      repair engine a methodology definition/instance-rule change already uses — which
//      mints a new revision, refreshes the search/meta index, and never touches a value that
//      was already valid. A `work`-kind board's empty type is left alone on purpose:
//      MethodologyRuntime.For declares type mandatory for `work` (no implicit default), so
//      guessing one here would override a deliberate "type required" contract rather than
//      repair a gap.
//
// Runs per project file, per board, resolved through that board's OWN methodology
// (ITasksService.GetRuntimeForBoardAsync) so a project's own rules — not just the built-in
// presets — decide the default type / initial status.
public sealed class NodeIdentityBackfillMigrator
{
	readonly ICoreDbFactory _dbf;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly ITasksService _tasks;
	readonly TaskBoardStore _boards;
	readonly MethodologyLiveMigration _live;
	readonly CommentService _comments;
	readonly ILogger? _log;

	const string ReasonText =
		"legacy-node-empty-nodeid-404: this node's identity was repaired by the startup backfill " +
		"(NodeId assigned and/or type/status defaulted) so it is addressable again.";

	public NodeIdentityBackfillMigrator(ICoreDbFactory dbf, IScopedDbFactory<TasksDb> factory, ITasksService tasks, ILogger? log = null)
	{
		_dbf = dbf;
		_factory = factory;
		_tasks = tasks;
		_boards = new TaskBoardStore(dbf, factory);
		_live = new MethodologyLiveMigration(_boards);
		_comments = new CommentService(factory);
		_log = log;
	}

	// Returns the number of nodes touched (NodeId patched and/or type/status repaired), summed
	// across every project. See LastRun for per-project touched/malformed/failed counts.
	StartupMigrationRun.Result LastRun { get; set; }

	public int Migrate()
	{
		using var db = _dbf.Open();
		var projects = StartupMigrationRun.DiscoverProjects(db, _factory.BaseDir);
		LastRun = StartupMigrationRun.Execute("node-identity-backfill", projects, MigrateProject, _log);
		return LastRun.DocumentsTouched;
	}

	StartupMigrationRun.ProjectOutcome MigrateProject(string projectKey)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		var boards = _boards.ListAsync(projectKey).GetAwaiter().GetResult();
		var touched = 0;
		var malformed = 0;

		foreach (var board in boards)
		{
			// Phase 1 — NodeId: a raw column patch (see class comment for why an ordinary
			// upsert cannot do this), applied before phase 2 ever reads the row so the
			// type/status repair below always sees the corrected id.
			var missingId = ctx.TaskNodes
				.Where(n => n.Board == board.Name && n.ActiveTo == null && n.NodeId == "")
				.Select(n => n.Key)
				.ToList();
			foreach (var key in missingId)
			{
				var freshId = Guid.NewGuid().ToString("N");
				ctx.TaskNodes.Where(n => n.Board == board.Name && n.Key == key && n.ActiveTo == null)
					.Set(n => n.NodeId, _ => freshId)
					.UpdateAsync().GetAwaiter().GetResult();
				_log?.LogWarning(
					"Tasks node-identity-backfill: project {Project} board {Board} key {Key}: assigned NodeId {NodeId} (was empty)",
					projectKey, board.Name, key, freshId);
			}

			// Phase 2 — Type/Status: re-read (now NodeId-complete) active rows and repair any
			// empty type or off-FSM status through this board's own resolved runtime.
			var runtime = _tasks.GetRuntimeForBoardAsync(projectKey, board.Name).GetAwaiter().GetResult();
			var active = ctx.TaskNodes.Where(n => n.Board == board.Name && n.ActiveTo == null).ToList();
			var toFix = new List<TaskNode>();
			foreach (var n in active)
			{
				// Mirrors TasksService.ApplyWorkflow's own empty-type resolution exactly: `For`
				// returns the WORKFLOW for an empty type (when the kind allows an implicit
				// default at all), not the resolved type string — DefaultType is the second,
				// separate call that names it, and the STORED value must be updated to match
				// (spec quick-add-stores-default-type: only a read used to re-derive the
				// default; the row itself must agree with it too).
				var type = n.Type;
				var wf = runtime.For(board.Kind, type.Length == 0 ? null : type);
				if (type.Length == 0 && wf is not null)
					type = runtime.DefaultType(board.Kind);
				if (wf is null)
				{
					// Either a non-empty type this runtime doesn't recognize (a project-specific
					// value we must not silently override — out of this migrator's declared
					// scope) or a kind that requires an explicit type (work) and has none. Either
					// way: leave it, log it, and count it so the pass is never silently lossy.
					malformed++;
					_log?.LogWarning(
						"Tasks node-identity-backfill: project {Project} board {Board} key {Key}: type '{Type}' does not resolve under kind '{Kind}' — left as-is, needs a human",
						projectKey, board.Name, n.Key, n.Type, board.Kind);
					continue;
				}

				var status = wf.Has(n.Status) ? n.Status : wf.Initial;
				if (type == n.Type && status == n.Status)
					continue; // already valid — nothing to repair

				toFix.Add(n with { Type = type, Status = status });
			}

			if (toFix.Count > 0)
			{
				_live.RewriteAsync(ctx, projectKey, board.Name, toFix, runtime, CancellationToken.None).GetAwaiter().GetResult();
				foreach (var n in toFix)
				{
					_comments.AddAsync(projectKey, board.Name, n.NodeId, parentId: null, author: "system",
						body: ReasonText, tags: ["artifact:reason"], ct: CancellationToken.None).GetAwaiter().GetResult();
					_log?.LogWarning(
						"Tasks node-identity-backfill: project {Project} board {Board} key {Key}: normalized type/status to '{Type}'/'{Status}'",
						projectKey, board.Name, n.Key, n.Type, n.Status);
				}
			}

			// One key can need both a NodeId patch (phase 1) and a type/status repair (phase 2) —
			// count it once either way.
			touched += missingId.Union(toFix.Select(n => n.Key), StringComparer.Ordinal).Count();
		}

		return new StartupMigrationRun.ProjectOutcome(touched, malformed);
	}
}
