using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Sessions.Data;
using PetBox.Tasks.Data;

namespace PetBox.Tests;

// ui-project-delete-orphan-files: ProjectDeletion cascades away a deleted project's Core-DB
// metadata but not its on-disk `.db` files for the tasks/memory/session temporal stores.
// The module orphan-cleanup services reclaim them via ProjectFileOrphans on a tick. These
// exercise that reclaim directly: a deleted project's files go; a live sibling survives; and
// the reserved built-ins ($system/$workspace) are never touched even without a Project row.
public sealed class ProjectFileOrphansTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;

	public ProjectFileOrphansTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-fileorphans-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));

		// A live sibling project the reclaim must never touch.
		_db.Insert(new Project { Key = "keep", WorkspaceKey = "ws", Name = "keep", Description = "" });
		// Prove the reserved guard (not merely the live-project set): drop the seeded $system /
		// $workspace Project rows so ONLY the ProjectDeletion.IsReserved guard can save their files.
		_db.Projects.Where(p => p.Key == "$system" || p.Key == "$workspace").Delete();
		// "gone" has neither a Project row nor a reserved key — the sole legitimate orphan.
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// A root-file-layout factory ({baseDir}/{scopeKey}.db) — tasks and sessions.
	ScopedDbFactory<TasksDb> TasksFactory() =>
		new(Path.Combine(_dir, "tasks"), Scope.Project,
			cs => new TasksDb(TasksDb.CreateOptions(cs)), TasksSchema.Ensure);

	ScopedDbFactory<SessionsDb> SessionsFactory() =>
		new(Path.Combine(_dir, "sessions"), Scope.Project,
			cs => new SessionsDb(SessionsDb.CreateOptions(cs)), SessionsSchema.Ensure);

	// A per-name-under-project-dir-layout factory ({baseDir}/{scopeKey}/{name}.db) — memory.
	ScopedDbFactory<MemoryDb> MemoryFactory() =>
		new(Path.Combine(_dir, "memory"), Scope.Project,
			cs => new MemoryDb(MemoryDb.CreateOptions(cs)), MemorySchema.Ensure);

	// Writes a fake `.db` file plus its WAL/SHM sidecars.
	static void FakeDb(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		foreach (var f in new[] { path, path + "-wal", path + "-shm" })
			File.WriteAllText(f, "x");
	}

	[Fact]
	public async Task ReclaimRootFiles_removes_only_the_deleted_projects_file()
	{
		var f = TasksFactory();
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "gone", null));
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "keep", null));
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "$system", null));

		var removed = await ProjectFileOrphans.ReclaimRootFilesAsync(_db, f, CancellationToken.None);

		removed.Should().BeEquivalentTo(new[] { "gone" });
		// The orphan's main file AND its sidecars are gone.
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "gone", null)).Should().BeFalse();
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "gone", null) + "-wal").Should().BeFalse();
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "gone", null) + "-shm").Should().BeFalse();
		// The live sibling survives.
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "keep", null)).Should().BeTrue();
		// $system survives purely on the reserved guard (its Project row was removed above).
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "$system", null)).Should().BeTrue();
	}

	[Fact]
	public async Task ReclaimRootFiles_covers_the_sessions_single_file_layout()
	{
		var f = SessionsFactory();
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "gone", null));
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "keep", null));

		var removed = await ProjectFileOrphans.ReclaimRootFilesAsync(_db, f, CancellationToken.None);

		removed.Should().BeEquivalentTo(new[] { "gone" });
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "gone", null)).Should().BeFalse();
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "keep", null)).Should().BeTrue();
	}

	[Fact]
	public async Task ReclaimProjectDirs_removes_the_deleted_projects_whole_memory_dir()
	{
		var f = MemoryFactory();
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "gone", "notes"));
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "gone", "canon"));
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "keep", "notes"));
		FakeDb(ScopedDbFiles.PathFor(f.BaseDir, "$workspace", "canon"));

		var removed = await ProjectFileOrphans.ReclaimProjectDirsAsync(_db, f, CancellationToken.None);

		removed.Should().BeEquivalentTo(new[] { "gone" });
		// Every store file of the deleted project — plus the now-empty directory — is gone.
		Directory.Exists(Path.Combine(f.BaseDir, "gone")).Should().BeFalse();
		// The live sibling and the reserved $workspace container survive intact.
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "keep", "notes")).Should().BeTrue();
		File.Exists(ScopedDbFiles.PathFor(f.BaseDir, "$workspace", "canon")).Should().BeTrue();
	}
}
