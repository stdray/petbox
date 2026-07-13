using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// EnsureWorkspaceContainerAsync is the new door: PetBox.Web.Pages.ProjectHome.MemoryModel used to open
// core.db itself just to call WorkspaceMemory.EnsureContainerAsync — this proves the wrapper does the
// same lazy, idempotent provisioning through the factory instead (the static helper's own behavior,
// incl. the concurrent-insert race, is covered exhaustively by WorkspaceMemoryTests; this file only
// proves the directory's method is correctly wired to it).
public sealed class WorkspaceMemoryDirectoryTests
{
	static (IWorkspaceMemoryDirectory Svc, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		return (new WorkspaceMemoryDirectory(dbf), dbf);
	}

	static void SeedWorkspace(ICoreDbFactory dbf, string key)
	{
		using var db = dbf.Open();
		db.Insert(new Workspace { Key = key, Name = key, Description = "", CreatedAt = DateTime.UtcNow });
	}

	[Fact]
	public async Task EnsureWorkspaceContainerAsync_MaterializesTheContainerRowOnFirstCall()
	{
		var (svc, dbf) = New();
		SeedWorkspace(dbf, "acme");

		await svc.EnsureWorkspaceContainerAsync("acme");

		using var db = dbf.Open();
		(await db.Projects.AnyAsync((Project p) => p.Key == "$ws-acme")).Should().BeTrue();
	}

	[Fact]
	public async Task EnsureWorkspaceContainerAsync_IsIdempotent_RepeatedCallsLeaveExactlyOneRow()
	{
		var (svc, dbf) = New();
		SeedWorkspace(dbf, "acme");

		// Two overlapping GETs of the memory page both ensure the same container — this must never
		// throw and must never leave two rows for one workspace's container.
		await svc.EnsureWorkspaceContainerAsync("acme");
		await svc.EnsureWorkspaceContainerAsync("acme");

		using var db = dbf.Open();
		(await db.Projects.CountAsync((Project p) => p.Key == "$ws-acme")).Should().Be(1);
	}

	[Fact]
	public async Task EnsureWorkspaceContainerAsync_NeverThrowsOnAnInvalidKey()
	{
		var (svc, _) = New();

		// Never-throw is load-bearing: this runs on a page GET (WorkspaceMemory.IsValidWorkspaceKey
		// governs it) — a bad key must not 500 the page.
		var act = async () => await svc.EnsureWorkspaceContainerAsync("not a valid key!");

		await act.Should().NotThrowAsync();
	}
}
