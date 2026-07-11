using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// Unit coverage for the per-workspace memory container helper (workspace-memory-isolation).
public sealed class WorkspaceMemoryTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;

	public WorkspaceMemoryTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-wsmem-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Theory]
	[InlineData("acme", true)]
	[InlineData("team-1", true)]
	[InlineData("a", true)]
	[InlineData("a:b", false)]
	[InlineData("a b", false)]
	[InlineData("a/b", false)]
	[InlineData("a\\b", false)]
	[InlineData("a*b", false)]
	[InlineData("a?b", false)]
	[InlineData("a\"b", false)]
	[InlineData("a<b", false)]
	[InlineData("a>b", false)]
	[InlineData("a|b", false)]
	[InlineData("a#b", false)]
	[InlineData("Foo", false)] // uppercase
	[InlineData("sys", false)] // reserved
	[InlineData("$system", false)] // not creatable via form
	[InlineData("", false)]
	[InlineData(null, false)]
	public void IsCreatableWorkspaceKey_Allowlist(string? key, bool expected) =>
		WorkspaceMemory.IsCreatableWorkspaceKey(key).Should().Be(expected);

	[Fact]
	public void IsValidWorkspaceKey_AllowsSystemAndCreatable()
	{
		WorkspaceMemory.IsValidWorkspaceKey("$system").Should().BeTrue();
		WorkspaceMemory.IsValidWorkspaceKey("acme").Should().BeTrue();
		WorkspaceMemory.IsValidWorkspaceKey("a:b").Should().BeFalse();
	}

	// ContainerKeyFor is on the layout render path — must never throw on a weird key
	// (would 500 every page of that workspace). Allowlist lives on create only.
	[Fact]
	public void ContainerKeyFor_NeverThrows_EvenOnJunk()
	{
		WorkspaceMemory.ContainerKeyFor("$system").Should().Be("$workspace");
		WorkspaceMemory.ContainerKeyFor("acme").Should().Be("$ws-acme");
		var act = () => WorkspaceMemory.ContainerKeyFor("a/b:c*");
		act.Should().NotThrow();
		WorkspaceMemory.ContainerKeyFor("a/b:c*").Should().Be("$ws-a/b:c*");
	}

	[Fact]
	public async Task EnsureContainerAsync_Idempotent_AndSkipsInvalid()
	{
		await WorkspaceMemory.EnsureContainerAsync(_db, "acme");
		_db.Projects.Count(p => p.Key == "$ws-acme").Should().Be(1);
		await WorkspaceMemory.EnsureContainerAsync(_db, "acme"); // second call no-op
		_db.Projects.Count(p => p.Key == "$ws-acme").Should().Be(1);

		// Invalid keys must not create Projects rows (soft no-op, not throw).
		await WorkspaceMemory.EnsureContainerAsync(_db, "a:b");
		_db.Projects.Any(p => p.Key.Contains(':')).Should().BeFalse();
	}

	// Concurrent-style double-insert: after Ensure, a direct Insert of the same PK must
	// leave Ensure's catch path able to re-run cleanly (row exists → swallow).
	[Fact]
	public async Task EnsureContainerAsync_SecondCallAfterRowExists_DoesNotThrow()
	{
		await WorkspaceMemory.EnsureContainerAsync(_db, "race-ws");
		// Simulate "another writer already inserted" by ensuring again — AnyAsync short-circuits.
		await WorkspaceMemory.EnsureContainerAsync(_db, "race-ws");
		var row = _db.Projects.Single(p => p.Key == "$ws-race-ws");
		row.WorkspaceKey.Should().Be("race-ws");
	}
}
