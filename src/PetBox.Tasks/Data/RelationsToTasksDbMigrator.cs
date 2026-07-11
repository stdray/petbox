using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tasks.Data;

// One-time, idempotent data migration for relations-in-project-db: copy the typed edges out
// of the Core DB's legacy `Relation` table (petbox.db, keyed by ProjectKey) into each
// project's own tasks file (tasks/{project}.db, table `relations`, no ProjectKey column).
//
// The source table is deliberately NOT dropped or emptied here — the DROP ships as a separate
// later release, once this backfill is verified against live data. That also makes the
// migration trivially RESUMABLE: the source is immutable, and each edge is copied under its
// ORIGINAL Id, so a re-run skips ids already present in the target and finishes a project
// that an interrupted run left half-copied. Re-running after the move is a no-op.
//
// DANGLING EDGES ARE DROPPED (owner's decision). An edge whose From/To NodeId has no node in
// that project's plan_nodes cannot be inserted at all — relations.From/ToNodeId is a real FK
// to the node-identity registry (M014_Relations). Such edges were only ever renderable as
// "missing", and they exist because deleting a board hard-deletes its nodes while the edges
// sat in another file, untouched. The loss is made VISIBLE, not silent: every dropped ACTIVE
// edge is logged individually (kind, from, to) at Warning, and the counts are logged per
// project and in a fleet-wide summary. A project with no tasks file has no nodes at all, so
// every one of its edges is dangling by definition — it is reported and no file is created
// for it. (Prod audit 2026-07-11: 1262 active edges, 9 dangling.)
public sealed class RelationsToTasksDbMigrator
{
	readonly PetBoxDb _core;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly string _tasksDir;
	readonly ILogger? _log;

	public RelationsToTasksDbMigrator(PetBoxDb core, IScopedDbFactory<TasksDb> factory, string tasksDir, ILogger? log = null)
	{
		_core = core;
		_factory = factory;
		_tasksDir = tasksDir;
		_log = log;
	}

	public sealed record Result(int Projects, int Copied, int Skipped, int DroppedActive, int DroppedClosed);

	public Result Migrate()
	{
		var byProject = _core.GetTable<LegacyRelation>()
			.ToList()
			.GroupBy(r => r.ProjectKey, StringComparer.Ordinal)
			.OrderBy(g => g.Key, StringComparer.Ordinal)
			.ToList();
		if (byProject.Count == 0) return new(0, 0, 0, 0, 0);

		int projects = 0, copied = 0, skipped = 0, droppedActive = 0, droppedClosed = 0;
		foreach (var group in byProject)
		{
			try
			{
				var r = MigrateProject(group.Key, group.ToList());
				if (r.Copied > 0 || r.DroppedActive > 0 || r.DroppedClosed > 0) projects++;
				copied += r.Copied;
				skipped += r.Skipped;
				droppedActive += r.DroppedActive;
				droppedClosed += r.DroppedClosed;
			}
			catch (Exception ex)
			{
				// Per-project isolation: one bad file must not stop the fleet. A failed project is
				// simply retried on the next start (the source rows are still there).
				_log?.LogError(ex, "Relations backfill FAILED for project {Project}; its edges stay in the Core DB and will be retried on the next start", group.Key);
			}
		}

		if (copied > 0 || droppedActive > 0 || droppedClosed > 0)
			_log?.LogInformation(
				"Relations backfill: {Copied} edge(s) moved into {Projects} project file(s), {Skipped} already present; DROPPED {DroppedActive} dangling ACTIVE edge(s) + {DroppedClosed} dangling closed edge(s) (endpoint node no longer exists)",
				copied, projects, skipped, droppedActive, droppedClosed);
		return new(projects, copied, skipped, droppedActive, droppedClosed);
	}

	Result MigrateProject(string project, List<LegacyRelation> edges)
	{
		// No tasks file => the project has no nodes => every edge it has is dangling. Report and
		// move on; do NOT create an empty file just to drop them all. ($workspace / $ws-* memory
		// pseudo-projects land here — they hold relation rows but no plan nodes.)
		if (!File.Exists(Path.Combine(_tasksDir, project + ".db")))
		{
			var (a, c) = (edges.Count(e => e.ClosedAt is null), edges.Count(e => e.ClosedAt is not null));
			foreach (var e in edges.Where(e => e.ClosedAt is null))
				LogDropped(project, e, "project has no tasks file (no nodes at all)");
			if (c > 0) _log?.LogInformation("Relations backfill: project {Project} has no tasks file — also dropped {Count} dangling closed edge(s)", project, c);
			return new(0, 0, 0, a, c);
		}

		using var db = _factory.GetDb(project); // ensures schema (M001..M014)

		// Node identities that actually exist in this file — the same set the FK consults.
		var nodes = db.GetTable<PlanNodeId>().Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);
		// Already-copied ids: what makes a re-run (or a resumed, interrupted run) a no-op.
		var present = db.GetTable<Relation>().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

		int copied = 0, skipped = 0, droppedActive = 0, droppedClosed = 0;
		foreach (var e in edges)
		{
			if (present.Contains(e.Id)) { skipped++; continue; }

			if (!nodes.Contains(e.FromNodeId) || !nodes.Contains(e.ToNodeId))
			{
				if (e.ClosedAt is null)
				{
					var which = !nodes.Contains(e.FromNodeId) && !nodes.Contains(e.ToNodeId) ? "both endpoints"
						: !nodes.Contains(e.FromNodeId) ? "from-node" : "to-node";
					LogDropped(project, e, $"{which} missing from plan_nodes");
					droppedActive++;
				}
				else
				{
					// History of an edge whose node is gone: dropped too (the FK cannot hold it),
					// but counted in aggregate rather than logged line by line.
					droppedClosed++;
				}
				continue;
			}

			db.Insert(new Relation
			{
				Id = e.Id, // original id => idempotent/resumable, and edge ids stay stable for callers
				Kind = e.Kind,
				FromNodeId = e.FromNodeId,
				ToNodeId = e.ToNodeId,
				CreatedAt = e.CreatedAt,
				ClosedAt = e.ClosedAt,
			});
			copied++;
		}

		if (copied > 0 || droppedActive > 0 || droppedClosed > 0)
			_log?.LogInformation(
				"Relations backfill [{Project}]: copied {Copied}, skipped {Skipped} (already there), dropped {DroppedActive} dangling active + {DroppedClosed} dangling closed",
				project, copied, skipped, droppedActive, droppedClosed);
		return new(1, copied, skipped, droppedActive, droppedClosed);
	}

	// The loss ledger: one line per ACTIVE edge that did not survive the move.
	void LogDropped(string project, LegacyRelation e, string why) =>
		_log?.LogWarning(
			"Relations backfill: DROPPED dangling active edge in {Project} — kind={Kind} from={FromNodeId} to={ToNodeId} (id={Id}, created={CreatedAt:u}): {Why}",
			project, e.Kind, e.FromNodeId, e.ToNodeId, e.Id, e.CreatedAt, why);
}
