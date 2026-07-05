using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Settings;

namespace PetBox.Core.Data;

// Tears down a project and every bookkeeping row it OWNS in the Core DB (petbox.db).
//
// Scope boundary (deliberate — see card ui-workspace-delete-cascade):
//   CASCADED (rows removed here): ApiKeys, HealthEndpoints, DataDbs, DataTables,
//     SavedQueries, ShareLinks, Logs (LogMeta), TaskBoards (meta), MemoryStores (meta),
//     Relations, project-scoped Settings, and the Project row itself.
//   NOT cascaded (per-project *files* on disk): DataDb and Log files are reclaimed by the
//     existing orphan-cleanup background services once their metadata rows are gone
//     (PetBox.Data.OrphanCleanupService / PetBox.Log.Core.LogOrphanCleanupService). Task
//     board / memory store / session `.db` files have NO such reclaimer and are left in
//     place — a follow-up may add file cleanup. HealthReports (tag-based, no ProjectKey
//     column) and ConfigBindings (tag-based, per-workspace file) are not FK'd to a project
//     and are not touched here.
public static class ProjectDeletion
{
	// Reserved built-in projects that must never be deleted. Mirrors the "$system" guard
	// used elsewhere; "$workspace" is the reserved cross-project memory container (M028/M031).
	public static readonly IReadOnlySet<string> ReservedProjects =
		new HashSet<string>(StringComparer.Ordinal) { "$system", "$workspace" };

	public static bool IsReserved(string projectKey) => ReservedProjects.Contains(projectKey);

	// Deletes the project's owned Core-DB rows and the project itself. Returns false when the
	// project does not exist. Callers are responsible for the reserved-entity guard.
	public static async Task<bool> DeleteAsync(PetBoxDb db, string projectKey, CancellationToken ct = default)
	{
		if (!await db.Projects.AnyAsync(p => p.Key == projectKey, ct))
			return false;

		await db.ApiKeys.Where(k => k.ProjectKey == projectKey).DeleteAsync(ct);
		await db.HealthEndpoints.Where(e => e.ProjectKey == projectKey).DeleteAsync(ct);
		await db.DataDbs.Where(d => d.ProjectKey == projectKey).DeleteAsync(ct);
		await db.DataTables.Where(t => t.ProjectKey == projectKey).DeleteAsync(ct);
		await db.SavedQueries.Where(q => q.ProjectKey == projectKey).DeleteAsync(ct);
		await db.ShareLinks.Where(s => s.ProjectKey == projectKey).DeleteAsync(ct);
		await db.Logs.Where(l => l.ProjectKey == projectKey).DeleteAsync(ct);
		await db.TaskBoards.Where(b => b.ProjectKey == projectKey).DeleteAsync(ct);
		await db.MemoryStores.Where(m => m.ProjectKey == projectKey).DeleteAsync(ct);
		await db.Relations.Where(r => r.ProjectKey == projectKey).DeleteAsync(ct);
		await db.Settings
			.Where(s => s.Scope == nameof(Scope.Project) && s.ScopeKey == projectKey)
			.DeleteAsync(ct);
		await db.Projects.Where(p => p.Key == projectKey).DeleteAsync(ct);
		return true;
	}
}
