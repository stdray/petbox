using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

public sealed class TaskBoardStoreTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;

	public TaskBoardStoreTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-taskboard-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Create_InsertsMeta_AndMaterializesFile()
	{
		var meta = await _store.CreateAsync("proj", "roadmap", "the plan");
		meta.Name.Should().Be("roadmap");
		(await _store.ExistsAsync("proj", "roadmap")).Should().BeTrue();
		// Boards share one per-project file now (not a file per board).
		File.Exists(ScopedDbFiles.PathFor(_factory.BaseDir, "proj", null)).Should().BeTrue();
	}

	[Fact]
	public async Task List_ReturnsCreatedBoards_Ordered()
	{
		await _store.CreateAsync("proj", "beta", null);
		await _store.CreateAsync("proj", "alpha", null);
		(await _store.ListAsync("proj")).Select(b => b.Name).Should().Equal("alpha", "beta");
	}

	[Fact]
	public async Task Delete_RemovesMeta()
	{
		await _store.CreateAsync("proj", "roadmap", null);
		(await _store.DeleteAsync("proj", "roadmap")).Should().BeTrue();
		(await _store.ExistsAsync("proj", "roadmap")).Should().BeFalse();
		// Physical file removal is best-effort (TryDelete bails on a Windows lock and
		// orphan-cleanup retries later); DeleteAsync only contracts that the metadata
		// is gone. Mirrors LogStore's delete coverage — see EntityToolsTests.
	}

	[Fact]
	public async Task Create_Duplicate_Throws() =>
		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await _store.CreateAsync("proj", "roadmap", null);
			await _store.CreateAsync("proj", "roadmap", null);
		});

	[Fact]
	public async Task Create_InvalidName_Throws() =>
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync("proj", "Bad Name", null));

	[Fact]
	public async Task Create_UnknownProject_Throws() =>
		await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateAsync("nope", "roadmap", null));

	[Fact]
	public async Task Touch_AdvancesUpdatedAt()
	{
		await _store.CreateAsync("proj", "roadmap", null);
		var past = DateTime.UtcNow.AddHours(-1);
		await _db.TaskBoards.Where(b => b.ProjectKey == "proj" && b.Name == "roadmap")
			.Set(b => b.UpdatedAt, past).UpdateAsync();

		await _store.TouchAsync("proj", "roadmap");

		(await _store.ListAsync("proj")).Single().UpdatedAt.Should().BeAfter(past);
	}

	[Fact]
	public async Task PlanNode_TemporalRoundtrip_ThroughBoardFile()
	{
		await _store.CreateAsync("proj", "roadmap", null);
		var ctx = _store.GetContext("proj");

		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new PlanNode { Board = "roadmap", Key = "Phase 1", Version = 0, Status = "InProgress", Body = "Foundation", Priority = 100 },
		}, partition: n => n.Board == "roadmap");
		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(1);

		var active = ctx.PlanNodes.Where(n => n.Board == "roadmap" && n.ActiveTo == null).ToList();
		active.Should().ContainSingle();
		active[0].Status.Should().Be("InProgress");
		active[0].Body.Should().Be("Foundation");
	}
}
