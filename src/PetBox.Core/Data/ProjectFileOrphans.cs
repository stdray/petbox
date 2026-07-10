using LinqToDB;
using LinqToDB.Data;

namespace PetBox.Core.Data;

// Reclaims a deleted project's per-project SQLite files from the scope-keyed temporal
// stores (tasks / memory / sessions). ProjectDeletion cascades away a project's Core-DB
// metadata rows but not its on-disk `.db` files; the module orphan-cleanup services call
// these helpers on a tick to mop them up — mirroring PetBox.Data.OrphanCleanupService /
// PetBox.Log.Core.LogOrphanCleanupService.
//
// Unlike those two (which reconcile disk files against per-name metadata rows), a
// tasks/sessions/memory file's lifecycle IS the owning project's: after a project delete
// there is no catalog left to diff against, so *project existence* is the orphan signal.
// This is also the safest signal — a live project's files are never touched, whatever the
// catalog state — which is why memory is reclaimed here per-project rather than per-store
// (individual store deletes already reclaim their file in MemoryStore.DeleteAsync).
//
// Reserved built-ins ($system / $workspace / $ws-*) are never swept: their files must
// survive regardless of catalog drift (see ProjectDeletion.IsReserved).
public static class ProjectFileOrphans
{
	// Single-file-per-project layout ({baseDir}/{scopeKey}.db, name == null): tasks, sessions.
	// Returns the project keys whose file was removed.
	public static async Task<IReadOnlyList<string>> ReclaimRootFilesAsync<TContext>(
		PetBoxDb db,
		IScopedDbFactory<TContext> factory,
		CancellationToken ct)
		where TContext : DataConnection
	{
		var live = await LiveProjectsAsync(db, ct);
		var removed = new List<string>();
		foreach (var scopeKey in ScopedDbFiles.ListRootScopeKeys(factory.BaseDir))
		{
			if (ct.IsCancellationRequested) break;
			if (ProjectDeletion.IsReserved(scopeKey) || live.Contains(scopeKey))
				continue;
			// Drop any cached (open) connection first — Windows keeps the file locked otherwise.
			await factory.EvictAsync(scopeKey);
			if (ScopedDbFiles.TryDelete(ScopedDbFiles.PathFor(factory.BaseDir, scopeKey, null)))
				removed.Add(scopeKey);
		}
		return removed;
	}

	// Per-name-under-project-dir layout ({baseDir}/{scopeKey}/{name}.db): memory. Removes every
	// `.db` file (+ sidecars) of an orphaned project, then the now-empty directory. Returns the
	// project keys fully reclaimed.
	public static async Task<IReadOnlyList<string>> ReclaimProjectDirsAsync<TContext>(
		PetBoxDb db,
		IScopedDbFactory<TContext> factory,
		CancellationToken ct)
		where TContext : DataConnection
	{
		var live = await LiveProjectsAsync(db, ct);
		var removed = new List<string>();
		foreach (var scopeKey in ScopedDbFiles.ListScopeKeys(factory.BaseDir))
		{
			if (ct.IsCancellationRequested) break;
			if (ProjectDeletion.IsReserved(scopeKey) || live.Contains(scopeKey))
				continue;

			var allGone = true;
			foreach (var name in ScopedDbFiles.ListNames(factory.BaseDir, scopeKey))
			{
				await factory.EvictAsync(scopeKey, name);
				if (!ScopedDbFiles.TryDelete(ScopedDbFiles.PathFor(factory.BaseDir, scopeKey, name)))
					allGone = false;
			}
			if (!allGone)
				continue; // a locked file remains — retry the whole dir next tick.

			var dir = Path.Combine(factory.BaseDir, scopeKey);
			try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: false); }
			catch (IOException) { /* non-empty / racing — harmless, files are already gone */ }
			catch (UnauthorizedAccessException) { }
			removed.Add(scopeKey);
		}
		return removed;
	}

	static async Task<HashSet<string>> LiveProjectsAsync(PetBoxDb db, CancellationToken ct) =>
		new(await db.Projects.Select(p => p.Key).ToListAsync(ct), StringComparer.Ordinal);
}
