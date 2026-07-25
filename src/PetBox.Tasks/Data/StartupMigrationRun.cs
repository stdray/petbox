using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data;

// Shared project discovery + per-project execution + aggregate observability for the startup
// document migrators that scan every project's stored methodology documents for a one-time,
// idempotent rewrite (LinkKindsDeclaredMigrator, WorkDeferredStatusMigrator,
// AutoWireFieldRenamedMigrator). Extracted (chore work/linkkinds-migrator-observability-gaps)
// because the three had grown byte-identical copies of this loop, all three sharing the SAME
// class of observability gap: a caught exception surfaced as only a per-project LogError line —
// nothing summed the pass up, so a startup regression (a project silently left un-migrated,
// spec-delivery quietly reading not_started, auto-wire quietly going dead, ...) needed a human
// grepping every routine startup log line to notice. A fourth hand-rolled copy would have been
// exactly how NOT to fix that, so this is the ONE place the loop + the tally + the aggregate log
// line live; each migrator's Migrate() just calls it.
//
// No new PUBLIC health surface: this is startup-log observability only (elevated log level +
// counters read straight off Migrate()'s caller via LastRun on each migrator). If a real startup-
// job health mechanism shows up later, THAT can wrap this — it does not need to invent one today.
public static class StartupMigrationRun
{
	// Every project a startup document migrator should visit: TaskBoards' ProjectKey (Core DB)
	// union every project that already has an on-disk tasks file (ScopedDbFiles.ListRootScopeKeys
	// — same single-file-per-project layout ProjectFileOrphans.ReclaimRootFilesAsync reads). A
	// project can carry methodology_defs/instances/templates rows with ZERO boards — every board
	// closed/deleted after the methodology was provisioned, or a document written before any board
	// existed — and TaskBoards alone never visits it. Visiting an extra project with nothing to
	// migrate costs nothing: MigrateProject no-ops on it (and NewEnsuredConnection only touches a
	// file that is already there — a project that never touched Tasks at all has no file in this
	// directory, so it is never opened, let alone created).
	public static IReadOnlyList<string> DiscoverProjects(PetBoxDb db, string tasksBaseDir) =>
		[.. db.TaskBoards.Select(b => b.ProjectKey).ToList()
			.Union(ScopedDbFiles.ListRootScopeKeys(tasksBaseDir), StringComparer.Ordinal)
			.OrderBy(k => k, StringComparer.Ordinal)];

	// One project's outcome: how many documents this migrator rewrote, and how many stored
	// documents it looked at but could not even parse (a "malformed document" — the migrator's own
	// Try* returned false because the JSON did not deserialize into the shape it expects, not
	// because there was nothing to migrate). Malformed is the "battled document" case NIT 1 names:
	// previously silent — Try* just returned false with zero log output, indistinguishable from the
	// common "nothing to do here" case.
	public readonly record struct ProjectOutcome(int Touched, int Malformed);

	// The whole pass's tally, returned by Migrate() via each migrator's LastRun property.
	public readonly record struct Result(
		int ProjectCount, int DocumentsTouched, int DocumentsMalformed, int ProjectsTouched, int ProjectsFailed);

	// Runs `migrateProject` once per discovered project, wrapped in the same try/catch every one of
	// these migrators already had (one bad project can't sink the whole pass), tallies the outcome,
	// and logs ONE elevated aggregate line when the pass finishes — Warning when anything needed a
	// human (a project threw, or a document was malformed), Information otherwise. The per-project
	// LogError on a thrown exception still fires too (unchanged): the aggregate line is a SUMMARY on
	// top of it, not a replacement.
	public static Result Execute(
		string migratorName,
		IReadOnlyList<string> projects,
		Func<string, ProjectOutcome> migrateProject,
		ILogger? log)
	{
		var documentsTouched = 0;
		var documentsMalformed = 0;
		var projectsTouched = 0;
		var projectsFailed = 0;
		foreach (var project in projects)
		{
			try
			{
				var outcome = migrateProject(project);
				documentsTouched += outcome.Touched;
				documentsMalformed += outcome.Malformed;
				if (outcome.Touched > 0) projectsTouched++;
			}
			catch (Exception ex)
			{
				projectsFailed++;
				log?.LogError(ex, "Tasks {Migrator} migration failed for project {Project}; left as-is", migratorName, project);
			}
		}

		var result = new Result(projects.Count, documentsTouched, documentsMalformed, projectsTouched, projectsFailed);
		const string Line =
			"Tasks {Migrator}: startup pass done — {DocumentsTouched} document(s) rewritten, {ProjectsTouched}/{ProjectCount} project(s) touched, {DocumentsMalformed} malformed document(s) skipped, {ProjectsFailed} project(s) FAILED";
		if (projectsFailed > 0 || documentsMalformed > 0)
			log?.LogWarning(Line, migratorName, documentsTouched, projectsTouched, projects.Count, documentsMalformed, projectsFailed);
		else
			log?.LogInformation(Line, migratorName, documentsTouched, projectsTouched, projects.Count, documentsMalformed, projectsFailed);
		return result;
	}
}
