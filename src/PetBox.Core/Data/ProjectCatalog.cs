using LinqToDB;
using LinqToDB.Async;

namespace PetBox.Core.Data;

// The ONE answer to "which projects exist" for background jobs (spec: catalog-is-source-of-truth).
//
// Per-project SQLite files are created LAZILY, on first write. The file system is therefore NOT a
// catalog, and a job that enumerates `{baseDir}/*.db` to learn its work list is wrong in BOTH
// directions:
//   missing — a project whose file does not exist yet is invisible to the scan, so the job skips
//     it silently (forever, if the tier's file is only written by the job itself);
//   ghost   — a deleted/renamed project's file lingers until the orphan-cleanup sweep, so the job
//     keeps working a project that no longer exists (burning LLM calls, and — for the jobs that
//     WRITE — resurrecting files the sweeper just reclaimed).
// The core-db catalog is written transactionally with the entity and cascaded on delete
// (ProjectDeletion), so it answers both. Ask it, don't scan the disk.
//
// Three lists, because the tiers differ in what their catalog is:
//   Projects     — the project catalog itself; the only answer for a tier with no per-entity
//                  catalog (sessions). A job driven off this list will OPEN (and thus create +
//                  migrate) the per-project file of every project — deliberate, see the jobs.
//   MemoryStores — memory's own catalog: a store row is written on explicit create AND on the
//                  auto-vivifying first write, so a project with any memory at all is in it.
//   TaskBoards   — the same for tasks.
// The per-entity lists are strictly narrower than Projects and keep the memory/tasks jobs from
// materializing an empty file for every project that never used that tier.
public interface IProjectCatalog
{
	// Every project key in core.db `Projects`, ordered. Includes the reserved built-ins: "$system"
	// and the "$workspace" / "$ws-*" memory containers are REAL Projects rows (see
	// WorkspaceMemory.EnsureContainerAsync), so a job driven off this list keeps seeing them —
	// exactly like the file scan it replaces.
	Task<IReadOnlyList<string>> ListProjectKeysAsync(CancellationToken ct = default);

	// Every workspace key in core.db `Workspaces`, ordered. The registry a `$ws-<key>` / `$workspace`
	// MEMORY CONTAINER is validated against: a container's Projects row is created LAZILY (on the first
	// resolve — WorkspaceMemory.EnsureContainerAsync), so the Projects list answers "does this container
	// exist yet", which is not the question — the question is whether it names a real workspace.
	Task<IReadOnlyList<string>> ListWorkspaceKeysAsync(CancellationToken ct = default);

	// Projects that have at least one memory store registered (core.db `MemoryStores`).
	Task<IReadOnlyList<string>> ListMemoryProjectKeysAsync(CancellationToken ct = default);

	// Projects that have at least one task board registered (core.db `TaskBoards`).
	Task<IReadOnlyList<string>> ListTaskProjectKeysAsync(CancellationToken ct = default);
}

public sealed class ProjectCatalog : IProjectCatalog
{
	readonly PetBoxDb _db;

	public ProjectCatalog(PetBoxDb db) => _db = db;

	public async Task<IReadOnlyList<string>> ListProjectKeysAsync(CancellationToken ct = default) =>
		await _db.Projects.Select(p => p.Key).OrderBy(k => k).ToListAsync(ct);

	public async Task<IReadOnlyList<string>> ListWorkspaceKeysAsync(CancellationToken ct = default) =>
		await _db.Workspaces.Select(w => w.Key).OrderBy(k => k).ToListAsync(ct);

	public async Task<IReadOnlyList<string>> ListMemoryProjectKeysAsync(CancellationToken ct = default) =>
		await _db.MemoryStores.Select(s => s.ProjectKey).Distinct().OrderBy(k => k).ToListAsync(ct);

	public async Task<IReadOnlyList<string>> ListTaskProjectKeysAsync(CancellationToken ct = default) =>
		await _db.TaskBoards.Select(b => b.ProjectKey).Distinct().OrderBy(k => k).ToListAsync(ct);
}
